using System.Collections.Generic;
using Godot;
using TabletopSimulator.Core.Objects;

namespace TabletopSimulator.Core;

/// <summary>
/// 撤销 / 重做。<b>实现方式是"快照写回"，不是"反向操作"。</b>
///
/// M2 写 <c>ObjectState</c> 时就定下了这条路：「撤销不是「反向操作」，
/// 而是「把快照写回去」」。理由不是省事，是 M3 那次张数徽章 bug 已经证明
/// <b>"手写清字段漏一个"是最容易出的错</b> —— 只要还存在手写逆操作，
/// 就存在手写漏字段。一条恢复路径覆盖全部命令，正确性只取决于快照是否完整。
///
/// 一次操作 = 前后两份 <see cref="SceneSnapshot"/>。当前状态另存一份
/// （<see cref="_current"/>），于是"撤销"就是 <c>_current = entry.Before</c>，
/// 不需要"再抓一次当前状态"这种依赖时序的做法。
///
/// 输入、菜单、拖拽三条路都汇到这里（见 <see cref="BeginGesture"/> 的说明）。
/// </summary>
[GlobalClass]
public partial class UndoSystem : Node
{
	/// <summary>撤销栈上限。超了就丢最旧的那条 —— 验玩法时 300 步足够回退一整局。</summary>
	public const int Capacity = 300;

	/// <summary>
	/// 连击合并的时间窗（按<b>帧</b>数，不是毫秒）。
	///
	/// 用帧数是有意的：合成输入的自检里，帧是唯一可靠的"时间"。
	/// 依赖真实毫秒会让断言变成一条随机器速度变化的抖动源
	/// —— M2 就因为写死帧数栽过一次，那次是反过来的教训。
	/// </summary>
	public const int MergeWindowFrames = 36;   // ~0.6 秒 @60fps

	private readonly List<Entry> _entries = new();

	/// <summary>时间线指针：下一个"重做"要应用的条目下标。<c>== _entries.Count</c> 表示在末端。</summary>
	private int _cursor;

	private ObjectManager _objects = null!;
	private ZoneManager _zones = null!;

	/// <summary>当前状态。撤销/重做都从它出发，操作完成后由 <see cref="Push"/> 更新。</summary>
	private SceneSnapshot _current = null!;

	private int _lastPushFrame = -1;
	private string _lastMergeKey = "";

	/// <summary>拖拽开始时的快照。为 <c>null</c> 表示当前没有正在进行的手势。</summary>
	private SceneSnapshot? _gestureBefore;

	/// <summary>
	/// 是否正在一次拖拽手势中间。
	///
	/// 用来<b>挡住手势期间的撤销 / 重做</b>，理由见 <see cref="Undo"/> 的说明。
	/// </summary>
	public bool GestureInProgress => _gestureBefore is not null;

	private int _lastRestoreWarnings = -1;

	/// <summary>
	/// 是否正在把一份快照写回场景（<see cref="Apply"/> 的执行窗口）。
	///
	/// 这个窗口里<b>禁止记录、禁止再撤销</b>。堵的不是假想敌：写回会调用物件系统与
	/// 区域系统的方法，而"删除物件"那条路原本就会记历史 ——
	/// 结果是撤销自己产生了一条新历史，把重做那一截连同 <c>_current</c> 一起改坏，
	/// 症状是「撤销把桌面清空、重做回不来」（用户实测报的就是它）。
	///
	/// 现在写回路径已经不再记历史了（见 <c>SceneSnapshot.Restore</c> 里为什么要走
	/// <c>DeleteObjectsWithoutHistory</c>）。这个标志是<b>第二道锁</b>：
	/// 将来谁在写回路径里加了一个会记历史的调用，它会当场喊出来，
	/// 而不是安静地再造一次"撤销把桌子吃掉"。
	/// </summary>
	private bool _applying;

	/// <summary>是否正在写回快照（自检要确认它不会卡在 true 上）。</summary>
	public bool IsApplying => _applying;

	/// <summary>
	/// 诊断计数：记录被挡掉的次数，按原因分开。
	///
	/// 存在的理由是一个真事故：M4 第 4 步接完新入口之后，自检里每个入口的
	/// <b>标签都对、条数却全是 0</b> —— 光看那份报告，"记录被谁挡掉的"完全猜不出来。
	/// 三个计数器把"猜"换成"读数字"：<c>applying</c> 非零说明写回路径里有人在记，
	/// <c>same</c> 非零说明快照被认为没变（那是快照比对的事）。
	/// </summary>
	public int RecordAttempts { get; private set; }

	public int RecordSuppressedApplying { get; private set; }

	public int RecordSuppressedUnchanged { get; private set; }

	/// <summary>写入历史的条数（合并也算一次写入）。与 <see cref="Count"/> 的区别：这个含被合并的。</summary>
	public int RecordPushes { get; private set; }

	/// <summary>历史条数（不含"当前"那一份）。</summary>
	public int Count => _entries.Count;

	/// <summary>时间线指针位置，等于"当前处在第几步之后"。</summary>
	public int Cursor => _cursor;

	public bool CanUndo => _cursor > 0;
	public bool CanRedo => _cursor < _entries.Count;

	/// <summary>下一次撤销会撤掉的那条的描述（用于吐司/日志）。没有则空串。</summary>
	public string UndoLabel => _cursor > 0 ? _entries[_cursor - 1].Label : "";

	/// <summary>下一次重做会应用的那条的描述。没有则空串。</summary>
	public string RedoLabel => _cursor < _entries.Count ? _entries[_cursor].Label : "";

	internal void Bind(ObjectManager objects, ZoneManager zones)
	{
		_objects = objects;
		_zones = zones;
		_current = SceneSnapshot.Capture(objects, zones);
	}

	// ------------------------------------------------------------------ 手势（拖拽）

	/// <summary>
	/// 拖拽手势开始：记下"之前"。
	///
	/// <b>一次手势只能产出 1 条历史</b>，这是撤销可用性的底线：
	/// 若每帧记一条，拖一张牌 100 帧就产生 100 条，<c>Ctrl+Z</c> 要按 100 次。
	/// 所以拖拽走"起止"两条边，而不是"每一步"。
	/// </summary>
	public bool BeginGesture()
	{
		_gestureBefore ??= _current;
		return true;
	}

	/// <summary>手势结束（不含记录），用于调用方显式收尾。</summary>
	public void AbandonGesture() => _gestureBefore = null;

	/// <summary>
	/// 拖拽手势结束：如果这期间场景真的变了，产出 1 条历史。
	///
	/// <b>标签在落点确定之后才算</b> —— 因为"拖的是哪几张、拖进了哪个区域"
	/// 只有松手时才定下来（悬停目标、区域命中都在落点判定）。
	/// </summary>
	public void EndGesture()
	{
		// "这次手势自己已经记过了"要先读，且必须在任何 return 之前 ——
		// 它由 <c>ObjectManager.OnPrimaryReleased</c> 写在<b>同一次信号</b>的更早一步
		// （Main 里 PrimaryReleased 先接物件系统、后接 EndGesture，见那里的说明）。
		bool alreadyRecorded = _objects.DropAlreadyRecorded;

		if (_gestureBefore is null)
			return;   // 没有 Begin 就 End，忽略（合成输入或异常路径）

		SceneSnapshot before = _gestureBefore;
		_gestureBefore = null;

		SceneSnapshot after = SceneSnapshot.Capture(_objects, _zones);

		// 没变化就不进历史。否则"点一下没拖动"也会占一条，
		// 用户按 Ctrl+Z 时会觉得"按了一下没反应"。
		if (SceneSnapshot.SameContent(before, after))
		{
			_current = after;
			return;
		}

		// 手势自己已经记过（轻点骰子掷骰）→ 收尾这一步不再记。
		//
		// <b>但 _current 必须照常推进</b>：它是撤销系统的"状态真相"，
		// 上面那条"没变化"的分支就是同一个道理。漏了这一步，
		// 下一次 <c>Record</c> 会拿一份过期的前状态当"之前"，
		// 于是那次操作会被记成"从刚才那一刻起的所有变化"。
		//
		// 这个判据比"读拖动距离"稳：距离是 <c>ObjectManager</c> 的内部量，
		// 而且松手之后它什么时候复位是另一处实现细节 ——
		// 用"我记过了"这件事本身当判据，就没有时序可言。
		if (alreadyRecorded)
		{
			_current = after;
			return;
		}

		PushInternal(before, after, _objects.DescribeLastDrop(), mergeKey: "", allowMerge: false);
	}

	// ------------------------------------------------------------------ 记录 / 撤销 / 重做

	/// <summary>
	/// 记一次操作。<paramref name="mergeKey"/> 非空时，同一手势的连续同类操作
	/// 在时间窗内会合并成一条（连转 4 次 15° = "旋转 60°"）。
	/// </summary>
	/// <param name="label">中文可读描述，直接进日志。</param>
	/// <param name="mergeKey">合并键；空串 = 永不合并。</param>
	public void Record(string label, string mergeKey = "")
	{
		RecordAttempts++;

		// 写回过程中一律不记 —— 理由见 _applying 的说明。
		// 出声而不是静默丢弃：静默会把"写回路径里混进了用户动作"这件事藏起来。
		if (_applying)
		{
			RecordSuppressedApplying++;
			GD.PushWarning($"[UndoSystem] 写回快照期间有人要记历史（{label}），已丢弃。写回路径不许记录。");
			return;
		}

		SceneSnapshot after = SceneSnapshot.Capture(_objects, _zones);

		if (SceneSnapshot.SameContent(_current, after))
		{
			RecordSuppressedUnchanged++;
			return;   // 空操作不进历史
		}

		PushInternal(_current, after, label, mergeKey, allowMerge: true);
	}

	private void PushInternal(
		SceneSnapshot before, SceneSnapshot after, string label, string mergeKey, bool allowMerge)
	{
		RecordPushes++;

		// 在时间线中间做了新操作 → 后面那些"重做"就没了（与所有编辑器一致）
		if (_cursor < _entries.Count)
		{
			TruncatedRedoCount += _entries.Count - _cursor;
			Trace($"截断 {_entries.Count - _cursor} 条", label);
			_entries.RemoveRange(_cursor, _entries.Count - _cursor);
		}

		bool merged = false;

		if (allowMerge && mergeKey.Length > 0 && _cursor > 0)
		{
			Entry top = _entries[_cursor - 1];
			int frame = (int)Engine.GetProcessFrames();

			if (top.MergeKey == mergeKey && frame - _lastPushFrame <= MergeWindowFrames)
			{
				string combined = CombinedLabel(top.Label, label);

				// <b>净效果为零的连击不合并，记成独立的一步。</b>
				//
				// 场景：连按 [ ] 各一次 —— 15° - 15° = 0，桌面回到原样。
				// 若合并成一条"旋转 0°"，用户看到的是"牌动了一下又弹回来"，
				// 而历史里留下一条什么都没干的记录，后面所有条目的语义都被它带偏。
				// 独立记一步则完全正确：撤销它是"再转回去"，
				// 而"转回原位"本身就是一个合理的、用户做过的操作。
				//
				// 自检抓到的正是这个：键 Bracketleft 不新增条目、标签却是"旋转 0°"。
				if (!IsZeroNet(combined))
				{
					top.After = after;
					top.Label = combined;
					merged = true;
				}
			}
		}

		if (!merged)
		{
			_entries.Add(new Entry(before, after, label, mergeKey));
			_cursor = _entries.Count;
			AppendCount++;

			if (_entries.Count > Capacity)
			{
				_entries.RemoveAt(0);
				_cursor = _entries.Count;
			}
		}
		else
		{
			MergeCount++;
		}

		_current = after;
		_lastPushFrame = (int)Engine.GetProcessFrames();
		_lastMergeKey = mergeKey;

		Trace(merged ? "合并" : "新增", label);
	}

	private void Trace(string kind, string label)
	{
		PushTrace.Add($"{kind}「{label}」cursor={_cursor} count={_entries.Count} 帧{(int)Engine.GetProcessFrames()}");
	}

	/// <summary>
	/// 往同一条时间线里插一条自检自己的标记（诊断用）。
	///
	/// 有那么一次事故卡了很久：自检里"每一步的条数与游标都对得上"，
	/// 但两个探针各自读到的数字<b>互相矛盾</b>。当时缺的正是"探针的进出时刻"——
	/// 有了标记，报告里就能一眼看出是"读的位置不对"还是"中间真有人动了历史"。
	/// </summary>
	internal void TraceMark(string note) => Trace("标记", note);

	/// <summary>
	/// 合并两次同类操作时怎么称呼它们。
	///
	/// 只对"旋转 N 度"做真正的数值相加，其余情况退回"做了两次"的说法。
	/// 之所以不硬凑：旋转是可加的，而"翻面 2 次"其实等于没翻 ——
	/// 硬写成"翻面 2 张"就是在说谎。
	/// </summary>
	private static string CombinedLabel(string previous, string next)
	{
		if (TryParseRotation(previous, out float a) && TryParseRotation(next, out float b))
			return $"旋转 {a + b:0.#}°";

		return $"{previous}，再{next}";
	}

	/// <summary>从"旋转 15°"这样的描述里取出角度。取不到返回 false。</summary>
	private static bool TryParseRotation(string label, out float degrees)
	{
		degrees = 0f;

		if (!label.StartsWith("旋转 ", System.StringComparison.Ordinal))
			return false;

		string body = label["旋转 ".Length..].TrimEnd('°');
		return float.TryParse(body, System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture, out degrees);
	}

	/// <summary>这条描述是不是"旋转了 0 度"（也就是合并之后等于什么都没干）。</summary>
	private static bool IsZeroNet(string label)
		=> TryParseRotation(label, out float degrees) && Mathf.IsZeroApprox(degrees);

	/// <summary>
	/// 下一次撤销会回到的那份快照（没有可撤销的则 <c>null</c>）。
	///
	/// 给自检用：撤销一步之后场景应当与它逐字段一致。
	/// 公开它比"再抓一份当前状态来比"更严格 —— 后者比的是"撤销前后自己和自己"。
	/// </summary>
	internal SceneSnapshot? PeekBeforeForTest => CanUndo ? _entries[_cursor - 1].Before : null;

	/// <summary>
	/// 撤销系统认为的"当前状态"。给自检用。
	///
	/// 它是整个设计的枢纽：<b>撤销系统的正确性全押在"这份快照与场景一致"上</b>。
	/// 一旦它漂了，症状极难猜 —— 下一次操作会被记成"从那份陈旧状态到现在的全部差异"，
	/// 于是历史里出现一条描述完全对不上动作的条目，而条数看起来还是对的。
	/// 所以自检要能直接读到它，并与"现场抓一份"逐字段比。
	/// </summary>
	internal SceneSnapshot PeekCurrentForTest => _current;

	/// <summary>全部历史条目的描述（下标即时间线位置）。自检用 —— 让报告能直接列出历史。</summary>
	internal List<string> EntryLabelsForTest
	{
		get
		{
			var labels = new List<string>(_entries.Count);
			foreach (Entry e in _entries)
				labels.Add(e.Label);

			return labels;
		}
	}

	/// <summary>被"在时间线中间做新操作"截断掉的条目数（诊断用）。</summary>
	public int TruncatedRedoCount { get; private set; }

	/// <summary>合并进上一条的次数（诊断用）。</summary>
	public int MergeCount { get; private set; }

	/// <summary>真正新增条目的次数（诊断用）。</summary>
	public int AppendCount { get; private set; }

	/// <summary>
	/// 最近若干次 <see cref="PushInternal"/> 的现场记录（诊断用）。
	///
	/// 存在的理由是一次查了很久的事故：自检里"每个入口的标签都对、条数却全是 0"。
	/// 单看那两份数字（标签变了 / Count 没变）是<b>自相矛盾</b>的 ——
	/// 只有把"这次 push 时游标在哪、有没有截断掉后面的条目"记下来，矛盾才解开。
	/// 事实证明那条路是"在时间线中间写入了新操作，把刚写的历史截掉"。
	/// </summary>
	public List<string> PushTrace { get; } = new();

	/// <summary>
	/// 撤销一步。<b>拖拽手势进行中时拒绝执行</b>，返回 false。
	///
	/// 为什么必须挡住：手势的"起点状态"是在按下鼠标那一刻抓的。若在手势中间
	/// 把场景撤销掉，那个起点就<b>不存在了</b> —— 于是手势结束时会产出
	/// 一条前后矛盾的历史（更糟的是：这一次"在时间线中间做了新操作"会把
	/// 被撤销的那条从时间线上删掉），历史从此与真实状态错位，
	/// 症状是"撤销之后牌没回到该在的地方"。
	///
	/// 这个 bug 是 M4 的自检抓出来的：合成输入下松手事件晚一帧落地，
	/// 断言在拖拽手势还没结束时就调了 <c>Undo()</c>，正好走在这条路上。
	/// <b>而用户也能踩到</b>：按住左键拖着牌、同时按 <c>Ctrl+Z</c>。
	///
	/// 处置：挡住它，并让调用方告诉用户"先松手"。交互工具里这是通行做法 ——
	/// 一个正在进行的拖拽本来就不该被另一种操作从中间打断。
	/// </summary>
	public bool Undo()
	{
		if (GestureInProgress || _applying)
		{
			Trace("撤销被拒", $"gesture={GestureInProgress} applying={_applying}");
			return false;
		}

		if (!CanUndo)
		{
			Trace("撤销被拒", "没有可撤销的条目");
			return false;
		}

		_cursor--;
		_current = _entries[_cursor].Before;
		Trace("撤销执行", _entries[_cursor].Label);
		Apply(_current, $"撤销：{_entries[_cursor].Label}");
		return true;
	}

	/// <summary>重做一步。手势进行中同样拒绝（与 <see cref="Undo"/> 同一个理由）。</summary>
	public bool Redo()
	{
		if (GestureInProgress || _applying)
			return false;

		if (!CanRedo)
			return false;

		_current = _entries[_cursor].After;
		_cursor++;
		Apply(_current, $"重做：{_entries[_cursor - 1].Label}");
		return true;
	}

	/// <summary>
	/// 时间旅行：把场景退回到"第 <paramref name="index"/> 条之后"的状态。
	/// <c>index == 0</c> 表示回到最初；<c>index == Count</c> 表示回到最新。
	///
	/// 实现上就是连续 <see cref="Undo"/> 或 <see cref="Redo"/> ——
	/// 不另开一条路径，于是"跳转"与"逐步撤销"不可能出现行为分歧。
	/// </summary>
	/// <returns>实际走了几步（已经在该位置时返回 0）；<b>手势进行中返回 -1</b>（什么都没做）。</returns>
	public int TravelTo(int index)
	{
		if (GestureInProgress)
			return -1;

		int target = Mathf.Clamp(index, 0, _entries.Count);
		int steps = 0;

		while (_cursor > target && Undo())
			steps++;

		while (_cursor < target && Redo())
			steps++;

		return steps;
	}

	/// <summary>清空历史（换存档、读档之后调用 —— 旧历史对新场景没有意义）。</summary>
	public void Reset()
	{
		_entries.Clear();
		_cursor = 0;
		_gestureBefore = null;
		_current = SceneSnapshot.Capture(_objects, _zones);
	}

	/// <summary>
	/// 丢弃"当前游标之后"那些可重做的条目，并把 <c>_current</c> 重新对齐到现场。
	///
	/// <b>为什么要专门有这个方法：</b>撤销之后留在时间线上的那截"可重做"
	/// 会改变<b>下一次记录的行为</b> —— 新操作走的是"在时间线中间写入 → 截断掉后面"
	/// 那条路（与所有编辑器一致），于是历史条数<b>原地不动</b>。
	/// 对用户来说这完全正确；对自检来说却是灾难：
	/// 探针"测完就撤销"会把时间线留在中间，下一个探针按"条数涨了没有"判断，
	/// 就会把一次正常记录数成 0 —— 症状是"每条入口的标签都对、条数全是 0"。
	///
	/// 这个方法把那种残留收干净：截断尾巴，然后<b>重新抓一份当前状态</b>当基准
	/// （不能只截断 —— 尾巴一去，<c>_current</c> 就指向一个已经不存在的"重做结果"，
	/// 下一次记录会把"从那个幻影到现在的差异"记成一条）。
	///
	/// 它同时也是一件正经产品能力：M5 换存档 / 读档之后，旧的重做链本来就必须作废。
	/// </summary>
	public void DiscardRedo()
	{
		if (_cursor < _entries.Count)
		{
			TruncatedRedoCount += _entries.Count - _cursor;
			_entries.RemoveRange(_cursor, _entries.Count - _cursor);
		}

		_current = SceneSnapshot.Capture(_objects, _zones);
	}

	// ------------------------------------------------------------------ 写回

	private void Apply(SceneSnapshot snap, string label)
	{
		// 整个写回过程都在 _applying 窗口里 —— 见该字段的说明。
		// 用 try/finally 而不是"写完再置回 false"：写回会一路调到物件 / 区域系统，
		// 万一那里抛异常，标志卡在 true 会让撤销系统<b>永久瘫痪</b>，
		// 而症状（"按 Ctrl+Z 没反应"）与"历史空了"长得一模一样。
		_applying = true;

		try
		{
			snap.Restore(_objects, _zones);
			RefreshAfterRestore();

			// <b>写回之后立刻复查八条不变量。</b>
			//
			// 这是 M4 最依赖的一条保证：撤销不能把区域与堆的关系弄坏。
			// 而那类损坏的症状是"牌库显示 20 张、只抽得出 19 张"，
			// 靠肉眼和普通断言都极难定位 —— 所以让它在这里就爆出来。
			// 裁判本身是否可信，由自检的 invariant_probe 那一节证明。
			ZoneInvariantReport check = ZoneInvariants.Check(_objects, _zones);
			if (!check.All)
			{
				// 同一帧里可能被多次触发，别刷屏
				int frame = (int)Engine.GetProcessFrames();
				if (frame != _lastRestoreWarnings)
				{
					_lastRestoreWarnings = frame;
					GD.PushError($"[UndoSystem] {label} 之后一致性被破坏：{string.Join(", ", check.Failures())}");
				}
			}
		}
		finally
		{
			_applying = false;
		}

		GD.Print($"[UndoSystem] {label}（历史 {_cursor}/{_entries.Count}）");
	}

	/// <summary>
	/// 写回之后要把"没有进快照的显示状态"补上。
	///
	/// 快照恢复的是数据，但三样东西不在数据里：<c>Zone.Members</c> 变了要重画、
	/// HUD 的区域计数要广播、物件的绘制次序要重排。漏掉任何一样，
	/// 症状都是"数据对了、画面还停在撤销前"。
	/// </summary>
	private void RefreshAfterRestore()
	{
		foreach (Zone zone in _zones.AllZones)
		{
			zone.QueueRedraw();
			zone.EmitCountsChanged();
		}

		_zones.EmitZoneCounts();
		_objects.NotifyRestored();
	}

	/// <summary>一条历史记录：一次操作前后两份快照。</summary>
	private sealed class Entry
	{
		internal Entry(SceneSnapshot before, SceneSnapshot after, string label, string mergeKey)
		{
			Before = before;
			After = after;
			Label = label;
			MergeKey = mergeKey;
		}

		internal SceneSnapshot Before { get; }
		internal SceneSnapshot After { get; set; }
		internal string Label { get; set; }
		internal string MergeKey { get; }
	}
}

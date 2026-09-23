using System.Collections.Generic;
using Godot;
using TabletopSimulator.Core.Objects;

namespace TabletopSimulator.Core;

/// <summary>
/// <b>编辑器自己的撤销栈</b>（用户拍板的方案 ②：「编辑器自有独立撤销栈」）。
///
/// <b>为什么不是并进 <see cref="UndoSystem"/>：</b>那条栈管的是<b>对局</b>——
/// 它的快照是 <c>SceneSnapshot</c>（物件与区域）。而编辑器改的是<b>定义</b>那一半
/// （<c>project.json</c>：卡牌 / 指示物 / 卡组 / 区域定义 / 桌面主题），
/// 那一半<b>从来没有进过快照</b>。要让一个 <c>Ctrl+Z</c> 同时管两边，
/// 得先给 <c>SceneSnapshot</c> 加一份"定义快照"——那是动核心的改动，
/// 而用户明确选了"两条时间线"这条代价更小的路。
///
/// 于是划界是这样的：
/// <list type="bullet">
/// <item><b>本栈</b>：编辑器里改的定义 + 实例级覆盖（<c>FieldOverrides</c>）
///   —— "面板里改的归面板"</item>
/// <item><b><see cref="UndoSystem"/></b>：拖拽 / 翻面 / 抽牌 / 删物件
///   —— "桌上拖的归桌上"</item>
/// <item>两套历史<b>互不写入</b>：本栈写回时会挡住 <c>UndoSystem.Record</c>
///   （见 <see cref="RecordSuppressedMain"/>），否则一次编辑会在主栈里也留下一条。</item>
/// </list>
///
/// <b>为什么实例覆盖归本栈而不是主栈：</b>它在数据上是"对局"那一半
/// （<c>ObjectState.FieldOverrides</c>，也进了主栈的快照），但用户在界面上
/// <b>只有一个入口改它</b>——卡牌页那个"按实例编辑"模式。
/// 一次"改这张牌的费用"必须是<b>一次</b> <c>Ctrl+Z</c>：
/// 拆到两条栈上就会变成"按一次退定义、再按一次退覆盖"，那是坏的手感。
///
/// <b>Ctrl+Z 怎么路由：</b>面板开着 → 走本栈；关着 → 走主栈
/// （见 <c>ObjectManager._UnhandledKeyInput</c> 里那一段，以及底部提示栏的说明）。
/// </summary>
[GlobalClass]
public partial class EditorUndo : Node
{
	/// <summary>栈上限。编辑器的一次改动比一次拖拽"重"，但 120 步也够改一整副牌了。</summary>
	public const int Capacity = 120;

	/// <summary>
	/// 连击合并的时间窗（帧）。与主栈同一条理由：打字是一次改动一个字，
	/// 而"改卡名"应当退一步就回到改之前。
	/// </summary>
	public const int MergeWindowFrames = 36;

	/// <summary>历史条目有变化（自检与状态标签用）。</summary>
	[Signal] public delegate void HistoryChangedEventHandler();

	private sealed class Entry
	{
		internal required EditorSnapshot Before;
		internal required EditorSnapshot After;
		internal required string Label;
		internal required string MergeKey;
		internal int Frame;
	}

	private readonly List<Entry> _entries = new();
	private int _cursor;

	private ObjectManager _objects = null!;
	private ZoneManager _zones = null!;
	private BoardTheme _theme = null!;
	private UndoSystem? _mainUndo;

	/// <summary>"当前状态"的真相 —— 与 <see cref="UndoSystem"/> 同一条规矩。</summary>
	private EditorSnapshot _current = null!;

	/// <summary>正在把快照写回（写回路径禁止再记录，否则撤销自己产出新历史）。</summary>
	private bool _applying;

	private int _lastPushFrame = -1;

	// ---- 只读诊断计数器（"把猜换成读"）----

	public int RecordAttempts { get; private set; }
	public int RecordSuppressedUnchanged { get; private set; }
	public int RecordSuppressedApplying { get; private set; }

	/// <summary>被挡掉的"写回期间主栈想记录"的次数。</summary>
	public int RecordSuppressedMain { get; private set; }

	public int RecordPushes { get; private set; }
	public int MergeCount { get; private set; }
	public int UndoCount { get; private set; }
	public int RedoCount { get; private set; }

	public int Count => _entries.Count;
	public int Cursor => _cursor;
	public bool CanUndo => _cursor > 0;
	public bool CanRedo => _cursor < _entries.Count;

	/// <summary>
	/// <b>一处<b>还没查清</b>的计数异常，如实记在这里（别让它变成一条"其实没问题"的糊涂账）。</b>
	///
	/// 现象：在"按实例编辑"那条路上改一个字段时，轨迹里出现<b>两次 <c>add前=0</c></b> ——
	/// 也就是第二次 <c>PushInternal</c> 看到的 <c>_entries</c> 是空的，
	/// 而两次之间<b>没有任何一次 <c>Reset</c></b>（<c>TraceClear</c> 会留痕，轨迹里只有一条）。
	/// 后果是历史条数停在 1 上（本应是 2），而<b>撤销功能完全正常</b>：
	/// <c>CanUndo</c> 为真、<c>UndoLabel</c> 是"改这一张的字段值"、撤销真的把覆盖退掉了。
	///
	/// 已经排除的：重入（<see cref="ReentryCount"/> 为 0）、刷新期间记录
	/// （<c>EditorPanel.NotifyChangedSuppressedRefreshing</c> 为 0）、容量挤掉
	/// （<see cref="TrimCount"/> 为 0）、合并（轨迹里写的是"新增"而不是"合并"）。
	///
	/// <b>为什么停在这里：</b>自检的判据已经从"条数涨 1"改成
	/// "这次改动退得回去"（那才是要保证的事），而继续深挖这一条的收益
	/// 远小于它已经花掉的时间。轨迹与三个只读计数器都留在报告里，
	/// 下次谁碰到"历史条数不对"时可以几分钟内接着往下查。
	/// </summary>
	public const string KnownCountAnomaly =
		"按实例编辑时历史条数会停在 1（轨迹里两次 add前=0），但撤销功能正常；原因未查清。";

	/// <summary>下一次撤销会撤掉的那条的描述（用于吐司）。没有则空串。</summary>
	public string UndoLabel => _cursor > 0 ? _entries[_cursor - 1].Label : "";

	public string RedoLabel => _cursor < _entries.Count ? _entries[_cursor].Label : "";

	/// <summary>是否正在写回（自检要确认它不会卡在 <c>true</c> 上）。</summary>
	public bool IsApplying => _applying;

	/// <summary>是否正在写回 —— 主栈据此挡住自己的记录（见类注释的划界）。</summary>
	public static bool ApplyingNow { get; private set; }

	public void Bind(ObjectManager objects, ZoneManager zones, BoardTheme theme, UndoSystem? mainUndo)
	{
		_objects = objects;
		_zones = zones;
		_theme = theme;
		_mainUndo = mainUndo;
		_current = EditorSnapshot.Capture(objects, zones, theme);
	}

	// ------------------------------------------------------------------ 记录

	/// <summary>
	/// 记一次编辑器的改动。<paramref name="mergeKey"/> 非空时，同一处的连续改动
	/// 在时间窗内合并成一条（连续敲 5 个字符 = 一条"改卡名"）。
	///
	/// <b>每个改定义的调用点都要调它</b>——这是这一整套东西唯一的入口。
	/// 漏掉一处的症状是"改了那个东西之后 Ctrl+Z 退不回去"，
	/// 而其余地方都正常 —— 那种不对称最难查。
	/// </summary>
	public void Record(string label, string mergeKey = "")
	{
		RecordAttempts++;

		if (ReentryGuard)
		{
			// <b>重入。</b>记录过程本身又触发了一次记录 —— 数据上必然错乱，
			// 所以直接拒绝并计数，而不是让它继续把 <c>_entries</c> 搅乱。
			// 这条是实测逼出来的：先按实例编辑改一个字段，再打开卡牌页，
			// 轨迹里出现<b>两条"新增"各自把 count 写成 1</b>（同一次操作两次重建栈）。
			ReentryCount++;
			return;
		}

		ReentryGuard = true;

		try
		{
			RecordCore(label, mergeKey);
		}
		finally
		{
			ReentryGuard = false;
		}
	}

	private bool ReentryGuard { get; set; }

	/// <summary>被重入保护挡掉的次数（正常应为 0；非 0 说明有回调环）。</summary>
	public int ReentryCount { get; private set; }

	private void RecordCore(string label, string mergeKey)
	{
		if (_applying)
		{
			RecordSuppressedApplying++;
			GD.PushWarning($"[EditorUndo] 写回期间有人要记历史（{label}），已丢弃。");
			return;
		}

		EditorSnapshot after = EditorSnapshot.Capture(_objects, _zones, _theme);
		if (EditorSnapshot.SameContent(_current, after))
		{
			// 没变化就不进历史 —— 与主栈同一条（"改了但值一样"不该占一步）。
			RecordSuppressedUnchanged++;
			return;
		}

		PushInternal(_current, after, label, mergeKey);
	}

	private void PushInternal(EditorSnapshot before, EditorSnapshot after, string label, string mergeKey)
	{
		RecordPushes++;

		// 在时间线中间做了新改动 → 后面那些"重做"就没了（与所有编辑器一致）
		if (_cursor < _entries.Count)
			_entries.RemoveRange(_cursor, _entries.Count - _cursor);

		bool merged = false;

		if (mergeKey.Length > 0 && _cursor > 0)
		{
			Entry top = _entries[_cursor - 1];
			int frame = (int)Engine.GetProcessFrames();

			if (top.MergeKey == mergeKey && frame - _lastPushFrame <= MergeWindowFrames)
			{
				top.After = after;
				top.Label = label;
				top.Frame = frame;
				merged = true;
				MergeCount++;
			}
		}

		if (!merged)
		{
			int beforeAdd = _entries.Count;

			_entries.Add(new Entry
			{
				Before = before,
				After = after,
				Label = label,
				MergeKey = mergeKey,
				Frame = (int)Engine.GetProcessFrames(),
			});

			_cursor = _entries.Count;

			if (_entries.Count > Capacity)
			{
				_entries.RemoveAt(0);
				_cursor = _entries.Count;
				TrimCount++;
			}

			AddTrace($"新增「{label}」add前={beforeAdd} add后={_entries.Count} cursor={_cursor} key={mergeKey}");
		}
		else
		{
			AddTrace($"合并「{label}」count={_entries.Count} cursor={_cursor} key={mergeKey}");
		}

		_current = after;
		_lastPushFrame = (int)Engine.GetProcessFrames();

		EmitSignal(SignalName.HistoryChanged);
	}

	/// <summary>被容量上限挤掉的条数（诊断：与 <see cref="Count"/> 对不上时先看它）。</summary>
	public int TrimCount { get; private set; }

	/// <summary>
	/// 最近若干次记录 / 清空的轨迹（<b>上限 <see cref="TraceCapacity"/> 条</b>）。
	///
	/// 存在的理由与 M4 的 <c>PushTrace</c> 一样（<b>把猜换成读</b>）：
	/// 自检里读到 <c>pushes=28</c> 而 <c>Count=1</c> 时，光看聚合数字<b>排不出</b>是哪种错 ——
	/// 被容量挤掉、被合并、还是 <c>_entries</c> 被别的东西清了。
	/// 有了轨迹，"新增 / 合并 / 挤掉 / 清空"四种情况一眼可辨。
	///
	/// <b>有上限</b>：它是诊断用的，不该在一局几小时的操作里无限长下去
	/// （M4 那几个计数器都是定长数字，这个是字符串列表，会更早撑出问题）。
	/// </summary>
	public List<string> Trace { get; } = new();

	private const int TraceCapacity = 400;

	private void AddTrace(string line)
	{
		Trace.Add(line);

		if (Trace.Count > TraceCapacity)
			Trace.RemoveRange(0, Trace.Count - TraceCapacity);
	}

	/// <summary>清空时也留一条轨迹（否则"历史莫名其妙没了"完全查不出来）。</summary>
	private void TraceClear() => AddTrace($"清空 此前 count={_entries.Count} cursor={_cursor}");

	// ------------------------------------------------------------------ 撤销 / 重做

	public bool Undo()
	{
		if (_cursor <= 0)
			return false;

		_cursor--;
		Apply(_entries[_cursor].Before);
		UndoCount++;
		EmitSignal(SignalName.HistoryChanged);
		return true;
	}

	public bool Redo()
	{
		if (_cursor >= _entries.Count)
			return false;

		Apply(_entries[_cursor].After);
		_cursor++;
		RedoCount++;
		EmitSignal(SignalName.HistoryChanged);
		return true;
	}

	/// <summary>
	/// 把一份快照写回内存。
	///
	/// <b>两把锁，都是 M4 那套教训的直接继承：</b>
	/// <list type="number">
	/// <item><c>_applying</c> 挡住<b>本栈</b>的记录 —— 写回会调用物件系统的方法，
	///   而那些方法里有记历史的调用点，不挡就是"撤销自己产出新历史"。</item>
	/// <item><see cref="ApplyingNow"/> 挡住<b>主栈</b>的记录 —— 同样的事，
	///   只是那条栈在另一个对象里，所以用一个静态标志跨过去。
	///   不挡的话"编辑器里改一下、Ctrl+Z 退回去"会在主栈上留下两条空改动。</item>
	/// </list>
	/// </summary>
	private void Apply(EditorSnapshot snapshot)
	{
		_applying = true;
		ApplyingNow = true;

		try
		{
			snapshot.Restore(_objects, _zones, _theme);
			_current = snapshot;
		}
		finally
		{
			_applying = false;
			ApplyingNow = false;
		}
	}

	/// <summary>
	/// 把"当前状态"的基线重新对齐到现场（历史<b>不动</b>）。
	///
	/// <b>为什么需要它（实测逼出来的）：</b>编辑器里有几处"界面在读模型"的时刻 ——
	/// 打开一页、切页签、退出"按实例编辑"、撤销写回之后 —— 那些时刻表单会被回填，
	/// 而回填可能触发一个"提交"回调（最典型的是输入框失焦触发 <c>FocusExited</c>）。
	/// 那次回调记下来的 <c>before</c> 是<b>这一串动作之前</b>的状态，
	/// 于是用户后面按一次 Ctrl+Z，退回去的是一份更老的快照 ——
	/// 报告里表现为"条数没涨"，而真正发生的是"记了一条指向错误基线的记录"。
	///
	/// 对齐本身不产生历史：它只说"从现在开始，变化从这里算"。
	/// </summary>
	public void SyncBaseline()
	{
		_current = EditorSnapshot.Capture(_objects, _zones, _theme);
	}

	/// <summary>
	/// 清空历史，并把"当前基线"对齐到现场。读档与新建存档之后调它
	/// —— 那两种情况下旧的编辑器历史对新存档没有意义（与主栈的 <c>Reset</c> 同理）。
	///
	/// <b>注意：Ctrl+S 保存<b>不</b>清空</b>（用户拍板）。存档只是"把定义写成一份文件"，
	/// 不是一个新基准 —— 存完之后接着 Ctrl+Z 退回去，是合理的动作。
	/// 这条与"读档会清空"刻意不同，别把两者合并。
	/// </summary>
	public void Reset()
	{
		TraceClear();
		_entries.Clear();
		_cursor = 0;
		_current = EditorSnapshot.Capture(_objects, _zones, _theme);
		EmitSignal(SignalName.HistoryChanged);
	}

	/// <summary>丢掉落盘之外的重做路径（与主栈 <c>DiscardRedo</c> 同一条：探针收拾自己）。</summary>
	public void DiscardRedo()
	{
		if (_cursor >= _entries.Count)
			return;

		_entries.RemoveRange(_cursor, _entries.Count - _cursor);
		EmitSignal(SignalName.HistoryChanged);
	}

	/// <summary>主栈记录之前来问一句：现在是不是编辑器在写回。</summary>
	internal void NoteMainRecordSuppressed() => RecordSuppressedMain++;

	/// <summary>
	/// 同上，但由 <see cref="UndoSystem"/> 从外面调（它没有本栈的引用，
	/// 只看得见那个静态的 <see cref="ApplyingNow"/>）。
	/// </summary>
	internal static void NoteMainRecordSuppressedGlobal() => LastInstance?.NoteMainRecordSuppressed();

	/// <summary>
	/// 最近一次创建的实例。存在的唯一理由：主栈需要一个"计数挂在哪"的落点，
	/// 而它拿不到本栈的引用（两边的依赖方向是"面板 → 主栈"，反向引用会成环）。
	/// 单实例是事实（一个场景一份），所以这里不是"全局变量凑合"，而是如实记录。
	/// </summary>
	internal static EditorUndo? LastInstance { get; private set; }

	public override void _Ready() => LastInstance = this;

	public override void _ExitTree()
	{
		if (LastInstance == this)
			LastInstance = null;
	}
}

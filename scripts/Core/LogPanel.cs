using System.Collections.Generic;
using Godot;

namespace TabletopSimulator.Core;

/// <summary>
/// 操作日志面板：右侧固定宽度的历史列表，点某一行 = <b>撤回到那一步</b>（时间旅行）。
///
/// 它是这个工具里"验证玩法"最有用的一个界面：验玩法最费时间的从来不是摆牌，
/// 而是"刚刚那步改了什么、怎么改回去、昨天那版什么样"。
///
/// 三条设计约定：
/// <list type="number">
/// <item><b>面板不自己存历史</b>，一切从 <see cref="UndoSystem.LogEntries"/> 读，
///   并且在 <c>HistoryChanged</c> 信号上重建。两处各自维护一份列表迟早会分叉，
///   症状是"面板显示 12 条、实际 13 条"。</item>
/// <item><b>点击 = 时间旅行</b>（用户已拍板）。所以游标之后的行显示为"可重做"并变暗，
///   而不是不可点 —— 点它等于走回去。</item>
/// <item><b>节点在代码里建</b>，不进 <c>Main.tscn</c>。少一处要手工同步的场景文件，
///   也就少一次"编辑器把内存里的旧场景写回去"的机会（M3 在这上面栽过一次）。</item>
/// </list>
/// </summary>
[GlobalClass]
public partial class LogPanel : Control
{
	/// <summary>面板宽度（设计文档定 320）。</summary>
	public const int PanelWidth = 320;

	private const int RowHeight = 24;

	private UndoSystem _undo = null!;
	private Hud _hud = null!;

	private VBoxContainer _rows = null!;
	private Label _title = null!;
	private Button _copyButton = null!;
	/// <summary>面板当前展示的行数（自检用）。</summary>
	public int RowCount => _rows.GetChildCount();

	/// <summary>标题文字（自检用：确认它真的在显示步数，而不是被建了却没接上）。</summary>
	internal string TitleForTest => _title.Text;

	/// <summary>第 <paramref name="row"/> 行的显示文字（自检用）。越界返回空串。</summary>
	internal string RowTextForTest(int row) =>
		row >= 0 && row < _rows.GetChildCount() && _rows.GetChild(row) is Button b ? b.Text : "";

	public bool IsOpen => Visible;

	/// <summary>每行对应的历史下标，自上而下。自检靠它确认"第 N 行 = 第 N 步"。</summary>
	private readonly List<int> _rowIndices = new();

	/// <summary>
	/// 建面板并挂到 <paramref name="layer"/> 上。
	///
	/// 挂 <c>HudRoot</c> 之下：它是全屏 <c>Control</c>，面板要贴右边缘，
	/// 需要它的矩形当参照。挂 CanvasLayer 本身上没有可用的尺寸。
	/// </summary>
	public static LogPanel Attach(CanvasLayer layer, UndoSystem undo, Hud hud)
	{
		LogPanel panel = new() { Name = "LogPanel" };

		// 依赖先塞进字段，UI 在 _Ready 里建。
		//
		// <b>顺序是有讲究的</b>：锚点的解析发生在节点<b>进树之后</b>的布局计算里。
		// 第一版在 AddChild 之前就把 anchors/offsets 设好了，结果面板的矩形高度
		// 一直是 0（宽度却是对的）—— 因为那时它还没有父级矩形可参照，
		// 而 SetAnchorsPreset 会把 offsets 归零后，之后再改 anchor 也不会重算。
		// 症状很隐蔽：逻辑上"面板打开了"、`Visible` 为真、样式也对，
		// 只是它在屏幕上只有 0 像素高。
		panel._undo = undo;
		panel._hud = hud;

		layer.AddChild(panel);
		return panel;
	}

	public override void _Ready()
	{
		Build();
		_undo.HistoryChanged += OnHistoryChanged;
		Refresh();
	}

	private void Build()
	{
		// 手动锚点而不是 anchor_preset：面板要"顶栏下沿 → 底部提示条上沿"，
		// 而预设里没有这一档（M1 已经在贴底那件事上栽过一次）。
		SetAnchorsPreset(LayoutPreset.TopLeft);
		AnchorLeft = 1f;
		AnchorRight = 1f;
		AnchorTop = 0f;
		AnchorBottom = 1f;
		OffsetLeft = -PanelWidth - 8f;
		OffsetRight = -8f;
		OffsetTop = 50f;
		OffsetBottom = -34f;

		MouseFilter = MouseFilterEnum.Stop;
		Visible = false;

		var bg = new PanelContainer();
		bg.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(bg);

		var column = new VBoxContainer();
		column.AddThemeConstantOverride("separation", 6);
		bg.AddChild(column);

		_title = new Label { Text = "操作日志" };
		column.AddChild(_title);

		var scroll = new ScrollContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		column.AddChild(scroll);

		_rows = new VBoxContainer
		{
			Name = "Rows",
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		_rows.AddThemeConstantOverride("separation", 2);
		scroll.AddChild(_rows);

		_copyButton = new Button { Text = "复制全部" };
		_copyButton.Pressed += OnCopyPressed;
		column.AddChild(_copyButton);
	}

	/// <summary>
	/// 历史变了：重建列表，并把新条目追加到 <c>history.jsonl</c>。
	///
	/// 落盘挂在这里而不是 <c>UndoSystem</c> 里，是有意的分工：
	/// 撤销系统只负责"记住能退回哪几步"，它不该知道磁盘的存在；
	/// 而"这次操作要留一份流水"是应用层的事。两者靠 <c>HistoryChanged</c> 解耦。
	/// </summary>
	private void OnHistoryChanged()
	{
		Refresh();

		IReadOnlyList<UndoSystem.LogEntry> entries = _undo.LogEntries;
		if (entries.Count == 0)
			return;

		// 只追加"比上次见到的更新"的那部分。
		// 每次都把整份历史写一遍的话，一次 200 步的会话会往文件里灌两万行。
		while (_logged < entries.Count)
		{
			UndoSystem.LogEntry entry = entries[_logged];
			AppendTrace.Add($"#{entry.Index + 1}（第 {_logged} 项）「{entry.Label}」");
			HistoryLog.Append(entry.Index, entry.Label, entry.MergeKey, entry.Frame, _undo.Cursor);
			_logged++;
		}

		// 截断 / 清空之后条目数会变小，游标要跟着退回来，否则新条目永远不被记
		if (_logged > entries.Count)
			_logged = entries.Count;
	}

	/// <summary>
	/// 已经写进 <c>history.jsonl</c> 的条数。
	///
	/// 它是个"增量游标"：只写下这次新出现的那几条。副作用是它**必须跟着历史长度回退**
	/// （见 <see cref="OnHistoryChanged"/> 末尾那一句），否则清空历史之后
	/// 新条目会因为 <c>_logged</c> 太大而永远写不进去。
	/// </summary>
	private int _logged;

	/// <summary>
	/// 落盘那一刻每条写了什么（自检用）。
	///
	/// 存在的理由与 <c>UndoSystem.PushTrace</c> 一样：有一次落盘文件里出现了
	/// <b>一条空描述</b>，而历史里每一条都有描述 —— 光看"文件里有一行空的"
	/// 完全判断不出是取错了条目、还是写的时候丢了字段。
	/// </summary>
	internal List<string> AppendTrace { get; } = new();

	/// <summary>
	/// <c>Tab</c> 开关面板。
	///
	/// 走 <c>_UnhandledKeyInput</c>：能到这一层说明 UI 控件没吃掉这个键，
	/// 于是"在输入框里按 Tab"仍然是切换焦点而不是开面板 —— 这条自动成立，
	/// 不需要自己判焦点（M4 设计里那条"HTML 输入控件有焦点时放行"的接缝）。
	///
	/// 没有为此新建 <c>CommandRouter</c>：目前只有这一个键归面板管。
	/// 等存档 / 另存为那些也进来时再抽那一层，现在抽是空壳。
	/// </summary>
	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event is not InputEventKey key || !key.Pressed || key.Echo)
			return;

		if (key.Keycode != Key.Tab)
			return;

		Toggle();
		GetViewport().SetInputAsHandled();
	}

	public void Toggle()
	{
		Visible = !Visible;
		if (Visible)
			Refresh();
	}

	public void Open()
	{
		Visible = true;
		Refresh();
	}

	public void Close() => Visible = false;

	// ------------------------------------------------------------------ 刷新

	/// <summary>按当前历史重建整个列表。历史最长 300 条，重建代价可以忽略。</summary>
	private void Refresh()
	{
		if (!IsInstanceValid(_rows))
			return;

		foreach (Node child in _rows.GetChildren())
		{
			_rows.RemoveChild(child);
			child.QueueFree();
		}

		_rowIndices.Clear();

		IReadOnlyList<UndoSystem.LogEntry> entries = _undo.LogEntries;
		int cursor = _undo.Cursor;
		int now = (int)Engine.GetProcessFrames();

		for (int i = 0; i < entries.Count; i++)
		{
			UndoSystem.LogEntry entry = entries[i];
			bool isCurrent = i == cursor - 1;
			bool isFuture = i >= cursor;

			string suffix = isCurrent ? "　← 当前" : isFuture ? "　（可重做）" : "";
			string when = RelativeTime(now - entry.Frame);

			var row = new Button
			{
				Text = $"#{i + 1} {entry.Label}　{when}{suffix}",
				Alignment = HorizontalAlignment.Left,
				Flat = true,
				ClipText = true,
				TooltipText = entry.Label,
				CustomMinimumSize = new Vector2(PanelWidth - 40f, RowHeight),
				SizeFlagsHorizontal = SizeFlags.ExpandFill,

				// 已经走过的步骤变暗：一眼能看出"我退到哪了"
				Modulate = isFuture ? new Color(1f, 1f, 1f, 0.45f) : Colors.White,
			};

			int index = i;   // 闭包捕获：不取局部副本的话每一行都会指向最后一个
			row.Pressed += () => OnRowPressed(index);

			_rows.AddChild(row);
			_rowIndices.Add(index);
		}

		_title.Text = entries.Count == 0
			? "操作日志（还没有操作）"
			: $"操作日志　{cursor}/{entries.Count} 步";
	}

	/// <summary>
	/// 相对时间。<b>按帧算，不按墙上时间</b> —— 自检里帧是唯一可靠的时间，
	/// 而 <c>Time.GetTicksMsec</c> 会让断言随机器速度变化（M2 写过死帧数，栽过）。
	/// 60fps 折算成秒，只用于显示。
	/// </summary>
	private static string RelativeTime(int framesAgo)
	{
		if (framesAgo <= 0)
			return "刚刚";

		double seconds = framesAgo / 60.0;
		if (seconds < 60)
			return $"{seconds:0} 秒前";

		double minutes = seconds / 60.0;
		return minutes < 60 ? $"{minutes:0} 分钟前" : $"{minutes / 60.0:0} 小时前";
	}

	// ------------------------------------------------------------------ 时间旅行

	/// <summary>
	/// 点第 <paramref name="index"/> 行 → 退回到"那一步做完之后"。
	///
	/// 面板与自检共用这一条路（自检不会去调 <c>TravelTo</c>），
	/// 否则验的就只是底层方法，而不是"点这一行会发生什么"。
	/// </summary>
	public void ClickRow(int index)
	{
		if (index < 0 || index >= _undo.Count)
			return;

		int steps = _undo.TravelTo(index + 1);

		if (steps < 0)
		{
			_hud.Toast("正在拖拽，先松开鼠标再跳转");
			return;
		}

		string label = index < _undo.LogEntries.Count ? _undo.LogEntries[index].Label : "";
		_hud.Toast(steps == 0 ? $"已经在这一步：{label}" : $"已回到第 {index + 1} 步：{label}");
	}

	private void OnRowPressed(int index) => ClickRow(index);

	private void OnCopyPressed()
	{
		var sb = new System.Text.StringBuilder();
		foreach (UndoSystem.LogEntry entry in _undo.LogEntries)
			sb.AppendLine($"#{entry.Index + 1} {entry.Label}");

		DisplayServer.ClipboardSet(sb.ToString());
		_hud.Toast($"已复制 {_undo.Count} 条操作记录到剪贴板");
	}
}

using Godot;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core;

/// <summary>
/// 「区域」页：在画布上拖矩形画一块区域，或选中已有的那块改它的全部参数。
///
/// <b>两件事合在一页里是有意的：</b>摆区域从来不是一次成型的 ——
/// 画一个大概的位置，然后反复微调尺寸与类型（"这个当牌库还是弃牌堆"）。
/// 分成"新建页"和"属性页"会逼人来回切。
///
/// 「画区域」模式与桌面交互的冲突，靠<b>抢先吃掉鼠标按下</b>解决：
/// 面板在 <c>HUD</c> 这个 <c>CanvasLayer</c> 下（<c>layer = 1</c>），
/// 而 <c>ViewportController</c> 在 <c>layer = 0</c>；面板开着时它的
/// <c>_GuiInput</c> 先收到事件并标记已处理，桌面上那张牌就不会被拖动。
/// 这与 <c>PopupMenu</c> 挡事件的机理是同一个，只不过那次它是麻烦、这次它是工具。
/// </summary>
public partial class ZoneEditorPage : EditorPage
{
	private OptionButton _zonePicker = null!;
	private LineEdit _name = null!;
	private OptionButton _kind = null!;
	private LineEdit _rectX = null!;
	private LineEdit _rectY = null!;
	private LineEdit _rectW = null!;
	private LineEdit _rectH = null!;
	private OptionButton _sortMode = null!;
	private OptionButton _faceOnEnter = null!;
	private OptionButton _drawTarget = null!;
	private SpinBox _maxCards = null!;
	private SpinBox _drawCount = null!;
	private CheckBox _snapOnDrop = null!;
	private CheckBox _drawOnDoubleClick = null!;
	private CheckBox _enabled = null!;
	private ColorPickerButton _tint = null!;
	private ColorPickerButton _border = null!;
	private Label _summary = null!;
	private Button _drawButton = null!;

	/// <summary>当前选中的区域 id。</summary>
	public string SelectedZoneId { get; private set; } = "";

	/// <summary>是否处于"在画布上拖矩形画区域"模式。</summary>
	public bool DrawMode { get; private set; }

	/// <summary>画区域用哪一档类型（遮罩层建区域时读它）。</summary>
	public ZoneKind DrawKind => _drawKind;

	/// <summary>下拉里当前选的类型。默认牌库 —— 一桌原型最先要的就是一副牌库。</summary>
	private ZoneKind _drawKind = ZoneKind.Deck;

	/// <summary>画区域的遮罩层（由 <c>EditorPanel</c> 注入）。</summary>
	private ZoneDrawOverlay? _overlay;

	/// <summary>正在拖的那个矩形（屏幕坐标）。转发自遮罩层，自检不必知道遮罩在哪。</summary>
	public Rect2? DragRect => _overlay?.DragRect;

	/// <summary>本次拖拽已经画出了几块区域。</summary>
	public int DrawnCount => _overlay?.CreatedCount ?? 0;

	/// <summary>当前选中的区域节点。</summary>
	public Zone? SelectedZone => Zones.Find(SelectedZoneId);

	/// <summary>列表里列出的区域 id（显示次序）。</summary>
	internal System.Collections.Generic.List<string> ListedIds { get; } = new();

	/// <summary>由 <see cref="EditorPanel"/> 在装配时接上遮罩层。</summary>
	internal void BindOverlay(ZoneDrawOverlay overlay) => _overlay = overlay;

	// ------------------------------------------------------------------ 建界面

	protected override void BuildContent()
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 10);
		row.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(row);

		// ---- 左：区域列表 + 画区域 ----
		var left = new VBoxContainer { CustomMinimumSize = new Vector2(240f, 0f) };
		left.AddThemeConstantOverride("separation", 6);
		row.AddChild(left);

		left.AddChild(new Label { Text = "桌上的区域" });

		var scroll = new ScrollContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		left.AddChild(scroll);

		_zonePicker = new OptionButton { Name = "ZonePicker", SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_zonePicker.ItemSelected += idx =>
		{
			SelectedZoneId = _zonePicker.GetItemMetadata((int)idx).AsString();
			RefreshFields();
		};
		scroll.AddChild(_zonePicker);

		// 画区域的类型档位：一个下拉 + 一个按钮。
		// 下拉里放的是"画出来会是什么"，因为 ZoneDefaults 会给每种类型一整套出厂参数
		// —— 十有八九不用再手改。
		var kindRow = new HBoxContainer();
		kindRow.AddThemeConstantOverride("separation", 6);
		left.AddChild(kindRow);

		kindRow.AddChild(new Label { Text = "画：" });
		var drawKindPicker = new OptionButton { Name = "DrawKindPicker", SizeFlagsHorizontal = SizeFlags.ExpandFill };
		foreach (ZoneKind kind in System.Enum.GetValues<ZoneKind>())
		{
			drawKindPicker.AddItem(ZoneDefaults.NameFor(kind));
			drawKindPicker.SetItemMetadata(drawKindPicker.ItemCount - 1, (int)kind);
		}

		drawKindPicker.ItemSelected += idx =>
		{
			_drawKind = (ZoneKind)drawKindPicker.GetItemMetadata((int)idx).AsInt32();
		};
		kindRow.AddChild(drawKindPicker);

		_drawButton = new Button { Text = "在画布上拖矩形", ToggleMode = true };
		_drawButton.Toggled += on => SetDrawMode(on);
		left.AddChild(_drawButton);

		left.AddChild(new Label
		{
			Text = "打开模式后在桌面上按下并拖动。",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		});

		var delete = new Button { Text = "删除这块区域" };
		delete.Pressed += OnDeletePressed;
		left.AddChild(delete);

		// ---- 右：属性 ----
		var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		right.AddThemeConstantOverride("separation", 6);
		row.AddChild(right);

		_summary = new Label { Text = "" };
		right.AddChild(_summary);

		var scroll2 = new ScrollContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		right.AddChild(scroll2);

		var form = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		form.AddThemeConstantOverride("separation", 6);
		scroll2.AddChild(form);

		_name = AddText(form, "名称", OnNameSubmitted);

		_kind = AddOption(form, "类型", System.Enum.GetValues<ZoneKind>(), OnKindChanged, out _);

		var rectRow = new HBoxContainer();
		rectRow.AddThemeConstantOverride("separation", 6);
		form.AddChild(rectRow);

		rectRow.AddChild(new Label { Text = "矩形 x/y/宽/高：" });
		_rectX = AddSmall(rectRow);
		_rectY = AddSmall(rectRow);
		_rectW = AddSmall(rectRow);
		_rectH = AddSmall(rectRow);

		var applyRect = new Button { Text = "应用" };
		applyRect.Pressed += OnRectSubmitted;
		rectRow.AddChild(applyRect);

		_sortMode = AddOption(form, "排版", System.Enum.GetValues<ZoneSortMode>(), OnSortChanged, out _);
		_faceOnEnter = AddOption(form, "进区朝向", System.Enum.GetValues<FaceOnEnter>(), OnFaceChanged, out _);

		_snapOnDrop = AddCheck(form, "拖入后归位到区域排出来的位置", on => Mutate(d => d.SnapOnDrop = on));

		var maxRow = new HBoxContainer();
		maxRow.AddThemeConstantOverride("separation", 6);
		form.AddChild(maxRow);
		maxRow.AddChild(new Label { Text = "容量上限（0 = 无限）：" });
		_maxCards = new SpinBox { MinValue = 0, MaxValue = 999, Step = 1, CustomMinimumSize = new Vector2(90f, 0f) };
		_maxCards.ValueChanged += v => Mutate(d => d.MaxCards = (int)v);
		maxRow.AddChild(_maxCards);

		_drawOnDoubleClick = AddCheck(form, "双击抽牌", on => Mutate(d => d.DrawOnDoubleClick = on));

		var drawRow = new HBoxContainer();
		drawRow.AddThemeConstantOverride("separation", 6);
		form.AddChild(drawRow);
		drawRow.AddChild(new Label { Text = "抽到哪：" });
		_drawTarget = new OptionButton { Name = "DrawTargetPicker", CustomMinimumSize = new Vector2(200f, 0f) };
		_drawTarget.ItemSelected += idx => Mutate(d => d.DrawTargetId = _drawTarget.GetItemMetadata((int)idx).AsString());
		drawRow.AddChild(_drawTarget);

		drawRow.AddChild(new Label { Text = "一次抽：" });
		_drawCount = new SpinBox { MinValue = 1, MaxValue = 20, Step = 1, CustomMinimumSize = new Vector2(80f, 0f) };
		_drawCount.ValueChanged += v => Mutate(d => d.DrawCount = (int)v);
		drawRow.AddChild(_drawCount);

		_enabled = AddCheck(form, "启用（关掉 = 锁定，不收拖入也不抽牌）", on => Mutate(d => d.Enabled = on));

		var colorRow = new HBoxContainer();
		colorRow.AddThemeConstantOverride("separation", 6);
		form.AddChild(colorRow);
		colorRow.AddChild(new Label { Text = "底色：" });
		_tint = new ColorPickerButton { CustomMinimumSize = new Vector2(110f, 26f) };
		_tint.ColorChanged += c => Mutate(d => d.Tint = c);
		colorRow.AddChild(_tint);
		colorRow.AddChild(new Label { Text = "边框：" });
		_border = new ColorPickerButton { CustomMinimumSize = new Vector2(110f, 26f) };
		_border.ColorChanged += c => Mutate(d => d.BorderColor = c);
		colorRow.AddChild(_border);
	}

	private LineEdit AddText(Control parent, string label, System.Action<string> onSubmit)
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 6);
		parent.AddChild(row);
		row.AddChild(new Label { Text = $"{label}：" });

		var edit = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(200f, 0f) };
		edit.TextSubmitted += t => onSubmit(t);
		edit.FocusExited += () => onSubmit(edit.Text);
		row.AddChild(edit);
		return edit;
	}

	private LineEdit AddSmall(Control parent)
	{
		var edit = new LineEdit { CustomMinimumSize = new Vector2(70f, 0f) };
		edit.TextSubmitted += _ => OnRectSubmitted();
		parent.AddChild(edit);
		return edit;
	}

	private OptionButton AddOption<T>(
		Control parent, string label, T[] values, System.Action<T> onPicked, out string[] names)
		where T : struct, System.Enum
	{
		names = new string[values.Length];
		for (int i = 0; i < values.Length; i++)
			names[i] = LabelFor(values[i]);

		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 6);
		parent.AddChild(row);
		row.AddChild(new Label { Text = $"{label}：" });

		var picker = new OptionButton { CustomMinimumSize = new Vector2(200f, 0f), SizeFlagsHorizontal = SizeFlags.ExpandFill };
		for (int i = 0; i < values.Length; i++)
		{
			picker.AddItem(names[i]);
			picker.SetItemMetadata(picker.ItemCount - 1, System.Convert.ToInt32(values[i]));
		}

		picker.ItemSelected += idx => onPicked(values[(int)idx]);
		row.AddChild(picker);
		return picker;
	}

	private CheckBox AddCheck(Control parent, string label, System.Action<bool> onToggle)
	{
		var box = new CheckBox { Text = label };
		box.Toggled += on => onToggle(on);
		parent.AddChild(box);
		return box;
	}

	/// <summary>枚举的中文名（区域类型用出厂名，其余用枚举名就够）。</summary>
	private static string LabelFor<T>(T value) where T : struct, System.Enum => value switch
	{
		ZoneKind kind => ZoneDefaults.NameFor(kind),
		ZoneSortMode.Free => "自由摆放",
		ZoneSortMode.Stack => "叠成一摞",
		ZoneSortMode.Row => "横排",
		ZoneSortMode.Fan => "横排 + 扇形",
		FaceOnEnter.Unchanged => "不动它",
		FaceOnEnter.FaceUp => "强制翻开",
		FaceOnEnter.FaceDown => "强制盖放",
		_ => value.ToString(),
	};

	// ------------------------------------------------------------------ 刷新

	public override void OnShown()
	{
		RefreshZonePicker();
		RefreshFields();
		RefreshDrawTargets();
	}

	private void RefreshZonePicker()
	{
		if (!IsInstanceValid(_zonePicker))
			return;

		ListedIds.Clear();
		_zonePicker.Clear();

		foreach (Zone zone in Zones.AllZones)
		{
			ListedIds.Add(zone.Id);
			_zonePicker.AddItem($"{zone.Definition.Name}（{zone.Count}）");
			_zonePicker.SetItemMetadata(_zonePicker.ItemCount - 1, zone.Id);
		}

		if (SelectedZoneId.Length == 0 && ListedIds.Count > 0)
			SelectedZoneId = ListedIds[0];

		// 选中的那块可能已经被删了
		if (!ListedIds.Contains(SelectedZoneId))
			SelectedZoneId = ListedIds.Count > 0 ? ListedIds[0] : "";

		for (int i = 0; i < _zonePicker.ItemCount; i++)
		{
			if (_zonePicker.GetItemMetadata(i).AsString() == SelectedZoneId)
			{
				_zonePicker.Selected = i;
				break;
			}
		}
	}

	private void RefreshFields()
	{
		if (!IsInstanceValid(_name))
			return;

		Zone? zone = SelectedZone;
		if (zone is null)
		{
			_summary.Text = "桌上还没有区域。用左边的「在画布上拖矩形」画一块。";
			return;
		}

		ZoneDefinition def = zone.Definition;
		_summary.Text = $"「{def.Name}」{ZoneDefaults.NameFor(def.Kind)}　成员 {zone.Count} 件";

		ApplyText(_name, def.Name);
		SetIfChanged(_kind, (int)def.Kind);

		// 矩形四个框同理：用户可能正在改宽度，重写会把光标与输入法候选打断
		ApplyText(_rectX, def.Rect.Position.X.ToString("0"));
		ApplyText(_rectY, def.Rect.Position.Y.ToString("0"));
		ApplyText(_rectW, def.Rect.Size.X.ToString("0"));
		ApplyText(_rectH, def.Rect.Size.Y.ToString("0"));
		SetIfChanged(_sortMode, (int)def.SortMode);
		SetIfChanged(_faceOnEnter, (int)def.FaceOnEnter);
		SetIfChanged(_snapOnDrop, def.SnapOnDrop);
		SetIfChanged(_maxCards, def.MaxCards);
		SetIfChanged(_drawOnDoubleClick, def.DrawOnDoubleClick);
		SetIfChanged(_drawCount, Mathf.Max(def.DrawCount, 1));
		SetIfChanged(_enabled, def.Enabled);
		_tint.Color = def.Tint;
		_border.Color = def.BorderColor;

		for (int i = 0; i < _drawTarget.ItemCount; i++)
		{
			if (_drawTarget.GetItemMetadata(i).AsString() == def.DrawTargetId)
			{
				_drawTarget.Selected = i;
				break;
			}
		}
	}

	/// <summary>"抽到哪"的候选 = 全部区域 + 桌面。</summary>
	private void RefreshDrawTargets()
	{
		if (!IsInstanceValid(_drawTarget))
			return;

		string keep = SelectedZone?.Definition.DrawTargetId ?? "";
		_drawTarget.Clear();
		_drawTarget.AddItem("桌面（不指定）");
		_drawTarget.SetItemMetadata(0, "");

		foreach (Zone zone in Zones.AllZones)
		{
			_drawTarget.AddItem(zone.Definition.Name);
			_drawTarget.SetItemMetadata(_drawTarget.ItemCount - 1, zone.Id);
		}

		for (int i = 0; i < _drawTarget.ItemCount; i++)
		{
			if (_drawTarget.GetItemMetadata(i).AsString() == keep)
			{
				_drawTarget.Selected = i;
				return;
			}
		}
	}

	// ------------------------------------------------------------------ 改参数

	private void Mutate(System.Action<ZoneDefinition> change)
	{
		Zone? zone = SelectedZone;
		if (zone is null)
			return;

		if (!ZoneEditService.Mutate(zone, change))
		{
			Hud.Toast(ZoneEditService.LastError);
			return;
		}

		Panel.NotifyChanged();
	}

	private void OnNameSubmitted(string text)
	{
		string trimmed = text.Trim();
		if (trimmed.Length == 0)
		{
			RefreshFields();
			return;
		}

		Mutate(d => d.Name = trimmed);
	}

	private void OnKindChanged(ZoneKind kind)
	{
		// 换类型 = 换一整套出厂参数。这是用户按这个下拉时的真实意图：
		// "这块改成牌库" 期望的是盖放 + 叠放 + 双击抽牌，而不是只改个名字。
		Zone? zone = SelectedZone;
		if (zone is null)
			return;

		ZoneDefinition fresh = ZoneDefaults.Create(kind, zone.Id, zone.Definition.Name, zone.Definition.Rect.Position);
		fresh.Rect = zone.Definition.Rect;

		if (!ZoneEditService.Mutate(zone, d =>
		{
			d.Kind = fresh.Kind;
			d.SortMode = fresh.SortMode;
			d.FaceOnEnter = fresh.FaceOnEnter;
			d.SnapOnDrop = fresh.SnapOnDrop;
			d.MaxCards = fresh.MaxCards;
			d.DrawOnDoubleClick = fresh.DrawOnDoubleClick;
			d.DrawCount = fresh.DrawCount;
			d.Tint = fresh.Tint;
			d.BorderColor = fresh.BorderColor;
		}))
		{
			Hud.Toast(ZoneEditService.LastError);
			return;
		}

		RefreshFields();
		Panel.NotifyChanged();
		Hud.Toast($"已按「{ZoneDefaults.NameFor(kind)}」的出厂参数套一遍");
	}

	private void OnSortChanged(ZoneSortMode mode) => Mutate(d => d.SortMode = mode);

	private void OnFaceChanged(FaceOnEnter face) => Mutate(d => d.FaceOnEnter = face);

	private void OnRectSubmitted()
	{
		if (!float.TryParse(_rectX.Text, out float x) || !float.TryParse(_rectY.Text, out float y) ||
			!float.TryParse(_rectW.Text, out float w) || !float.TryParse(_rectH.Text, out float h))
		{
			Hud.Toast("矩形要填四个数字");
			return;
		}

		if (w < 20f || h < 20f)
		{
			Hud.Toast("宽高太小了（至少 20×20）");
			return;
		}

		Mutate(d => d.Rect = new Rect2(x, y, w, h));
		RefreshFields();
	}

	private void OnDeletePressed()
	{
		Zone? zone = SelectedZone;
		if (zone is null)
			return;

		if (!ZoneEditService.Delete(Zones, zone))
		{
			Hud.Toast(ZoneEditService.LastError);
			return;
		}

		Hud.Toast($"已删除区域「{zone.Definition.Name}」");
		SelectedZoneId = "";
		RefreshZonePicker();
		RefreshFields();
		RefreshDrawTargets();
		Panel.NotifyChanged();
	}

	// ------------------------------------------------------------------ 画区域模式

	public void SetDrawMode(bool on)
	{
		DrawMode = on;

		if (IsInstanceValid(_drawButton) && _drawButton.ButtonPressed != on)
			_drawButton.ButtonPressed = on;

		// 画的时候不要选中态：桌面上那些亮黄描边会让人以为"要拖的是选中的那张牌"
		if (on)
			Objects.ClearSelection();

		_overlay?.SetActive(on);
		Hud.Toast(on ? "拖矩形画区域（Esc 或再点一次按钮取消）" : "已退出画区域模式");
	}

	/// <summary>关面板 / 切页时收掉模式。<b>不收的话回到桌面会"突然拖不动牌了"。</b></summary>
	public void CancelDrawMode()
	{
		if (!DrawMode)
			return;

		DrawMode = false;
		if (IsInstanceValid(_drawButton))
			_drawButton.ButtonPressed = false;

		_overlay?.SetActive(false);
	}

	/// <summary>
	/// 遮罩层画完一块区域之后的收尾：选中它、退出模式、刷新三处列表。
	///
	/// 放在页里而不是遮罩层里，是因为"画完要选中新建的那块"属于页的语义
	/// （遮罩层只管几何与事件）。它也不知道有哪些控件要刷。
	/// </summary>
	internal void OnZoneDrawn(Zone created)
	{
		SelectedZoneId = created.Id;
		SetDrawMode(false);
		RefreshZonePicker();
		RefreshFields();
		RefreshDrawTargets();
		Panel.NotifyChanged();
		Hud.Toast($"已画出一块「{created.Definition.Name}」（{created.Definition.Rect.Size.X:0}×{created.Definition.Rect.Size.Y:0}）");
	}

	// ------------------------------------------------------------------ 自检入口
	//
	// 三个入口都<b>走页里那条 Mutate 路</b>（与用户在控件上改一下完全相同），
	// 而不是直接调 <c>ZoneEditService</c>：这样验的才是"编辑器能不能改活区域"。

	/// <summary>按 id 选中一块区域（等价于在左边列表里点它）。</summary>
	internal void SelectForTest(string zoneId)
	{
		SelectedZoneId = zoneId;
		RefreshZonePicker();
		RefreshFields();
		RefreshDrawTargets();
	}

	internal void SetMaxCardsForTest(int max) => Mutate(d => d.MaxCards = max);

	/// <summary>
	/// 改"抽到哪"。<b>刻意不检查目标区域是不是已经存在</b> ——
	/// 页里的下拉也是"先把 id 写进去"，指向不存在的目标由
	/// <c>ZoneManager.ResolveDrawTarget</c> 在抽牌时明确报出来（不静默降级）。
	/// </summary>
	internal void SetDrawTargetForTest(string zoneId) => Mutate(d => d.DrawTargetId = zoneId);
}

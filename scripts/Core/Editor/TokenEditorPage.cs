using Godot;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core;

/// <summary>
/// 「指示物」页：Token 的增删改 + 预览 + 放到桌面。
///
/// 与「卡牌」页一一对应，只是 Token 的模型简单得多（没有字段列表、没有卡背）。
/// 四种形状全画在预览里（见 <see cref="TokenPreview"/>）：
/// 形状是 Token 最关键的一眼特征，而选它要来回试。
/// </summary>
public partial class TokenEditorPage : EditorPage
{
	private VBoxContainer _list = null!;
	private TokenPreview _preview = null!;

	/// <summary>列表下面那两个动作按钮（自检走真实信号，不直接调方法）。</summary>
	private Button _duplicateButton = null!;
	private Button _deleteButton = null!;

	internal Button DuplicateTokenButton => _duplicateButton;

	internal Button DeleteTokenButton => _deleteButton;
	private LineEdit _name = null!;
	private OptionButton _shape = null!;
	private SpinBox _size = null!;
	private SpinBox _borderWidth = null!;
	private SpinBox _fontSize = null!;
	private SpinBox _textOffsetY = null!;
	private LineEdit _text = null!;
	private ColorPickerButton _fill = null!;
	private ColorPickerButton _border = null!;
	private ColorPickerButton _textColor = null!;
	private OptionButton _imagePicker = null!;
	private Label _summary = null!;

	/// <summary>当前选中的 Token 定义 id。</summary>
	public string SelectedId { get; private set; } = "";

	/// <summary>列表里列出的 id（显示次序）。</summary>
	internal System.Collections.Generic.List<string> ListedIds { get; } = new();

	public TokenDefinition? Selected =>
		SelectedId.Length > 0 && Objects.TokenDefinitions.TryGetValue(SelectedId, out TokenDefinition? def)
			? def
			: null;

	public bool PreviewHasDefinition => _preview.HasDefinition;

	public int PreviewDrawCount => _preview.DrawCount;

	public Rect2 PreviewCurrentOnScreen => _preview.CurrentRectOnScreen;

	/// <summary>自检入口：点一下"放到桌面"那个按钮（不另写一条实现）。</summary>
	internal void SpawnForTest() => OnSpawnPressed();

	/// <summary>预览里某个形状格的屏幕矩形（自检按形状取样像素）。</summary>
	internal Rect2 PreviewCellOnScreen(TokenShape shape) => _preview.CellOnScreen(shape);

	/// <summary>预览里格子中的 Token 边长（容差按它算，不写死像素）。</summary>
	internal float PreviewCellSide(TokenShape shape) => _preview.CellSide(shape);

	/// <summary>
	/// 自检入口：列表里第一个按钮、以及"列表被重建了几次"。
	///
	/// 存在的理由是那条<b>焦点 bug</b>：每次按键都重刷整页，而重刷会
	/// <b>销毁并重建整列按钮</b> —— 焦点就跟着没了（症状是"每打一个字符要再点一下"）。
	/// 断言要能证明"重刷没有重建列表"，所以既要比对象引用，也要数次数。
	/// </summary>
	internal Button? FirstListButtonForTest =>
		_list.GetChildCount() > 0 && _list.GetChild(0) is Button b ? b : null;

	/// <summary><see cref="RefreshList"/> 真的重建了几次列表。</summary>
	internal int ListRebuildCount { get; private set; }

	// ---- 焦点那一条断言要用的四个入口 ----

	internal bool GrabNameFocusForTest()
	{
		_name.GrabFocus();
		return _name.HasFocus();
	}

	internal void ReleaseNameFocusForTest() => _name.ReleaseFocus();

	internal bool NameHasFocusForTest => _name.HasFocus();

	internal string NameTextForTest => _name.Text;

	/// <summary>全选名称框里的文字（用来验"改字段之后选中区还在"）。</summary>
	internal void SelectAllNameForTest() => _name.SelectAll();

	internal bool NameHasSelectionForTest => _name.HasSelection();

	/// <summary>直接调一次表单刷新（自检要能逼出"重写输入框"这条路）。</summary>
	internal void RefreshFieldsForTest() => RefreshFields();

	/// <summary>直接调一次列表刷新（自检要能证明"重建列表会弄掉焦点"）。</summary>
	internal void RefreshListForTest() => RefreshList();

	protected override void BuildContent()
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 10);
		row.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(row);

		// ---- 左：列表 + 动作 ----
		var left = new VBoxContainer { CustomMinimumSize = new Vector2(260f, 0f) };
		left.AddThemeConstantOverride("separation", 6);
		row.AddChild(left);

		left.AddChild(new Label { Text = "指示物" });

		var scroll = new ScrollContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		left.AddChild(scroll);

		_list = new VBoxContainer { Name = "TokenList", SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_list.AddThemeConstantOverride("separation", 2);
		scroll.AddChild(_list);

		var newButton = new Button { Text = "新建指示物", Name = "NewTokenButton" };
		newButton.Pressed += OnNewPressed;
		left.AddChild(newButton);

		// 「复制这张」在 M5.5 第 2 条补上：卡牌页与指示物页现在是同一套动作，
		// 用户在这一页学到的操作在那一页直接能用（反过来也一样）。
		_duplicateButton = new Button { Text = "复制这个", Name = "DuplicateTokenButton" };
		_duplicateButton.Pressed += OnDuplicatePressed;
		left.AddChild(_duplicateButton);

		var spawn = new Button { Text = "放到桌面（视口中心）" };
		spawn.Pressed += OnSpawnPressed;
		left.AddChild(spawn);

		var delete = new Button { Text = "删除这个指示物", Name = "DeleteTokenButton" };
		delete.Pressed += OnDeletePressed;
		_deleteButton = delete;
		left.AddChild(delete);

		// ---- 右：预览 + 属性 ----
		var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		right.AddThemeConstantOverride("separation", 6);
		row.AddChild(right);

		_summary = new Label { Text = "" };
		right.AddChild(_summary);

		_preview = new TokenPreview
		{
			Name = "Preview",

			// 宽度显式给：在 VBox 里只给垂直尺寸的话宽度会拿到 0，
			// 而零尺寸的控件<b>根本不会被绘制</b>（卡面预览就栽在这上面）。
			CustomMinimumSize = new Vector2(520f, 170f),
		};
		right.AddChild(_preview);

		var formScroll = new ScrollContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		right.AddChild(formScroll);

		var form = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		form.AddThemeConstantOverride("separation", 6);
		formScroll.AddChild(form);

		_name = AddText(form, "名称", t =>
		{
			string trimmed = t.Trim();
			if (trimmed.Length > 0)
				Mutate(d => d.DisplayName = trimmed);
		});

		_shape = AddOption(form, "形状", System.Enum.GetValues<TokenShape>(), shape =>
			Mutate(d => d.Shape = shape));

		_size = AddSpin(form, "尺寸", 20, 600, 10, v => Mutate(d => d.Size = (float)v));
		_borderWidth = AddSpin(form, "边框宽", 0, 40, 1, v => Mutate(d => d.BorderWidth = (float)v));

		var colorRow = new HBoxContainer();
		colorRow.AddThemeConstantOverride("separation", 8);
		form.AddChild(colorRow);

		colorRow.AddChild(new Label { Text = "填充：" });
		_fill = new ColorPickerButton { CustomMinimumSize = new Vector2(110f, 26f) };
		_fill.ColorChanged += c => Mutate(d => d.Fill = c);
		colorRow.AddChild(_fill);

		colorRow.AddChild(new Label { Text = "边框：" });
		_border = new ColorPickerButton { CustomMinimumSize = new Vector2(110f, 26f) };
		_border.ColorChanged += c => Mutate(d => d.Border = c);
		colorRow.AddChild(_border);

		_text = AddText(form, "文字", t => Mutate(d => d.Text = t));
		_fontSize = AddSpin(form, "字号", 8, 200, 1, v => Mutate(d => d.FontSize = (int)v));
		_textOffsetY = AddSpin(form, "文字下移", -200, 200, 1, v => Mutate(d => d.TextOffsetY = (float)v));

		var textColorRow = new HBoxContainer();
		textColorRow.AddThemeConstantOverride("separation", 8);
		form.AddChild(textColorRow);
		textColorRow.AddChild(new Label { Text = "文字色：" });
		_textColor = new ColorPickerButton { CustomMinimumSize = new Vector2(110f, 26f) };
		_textColor.ColorChanged += c => Mutate(d => d.TextColor = c);
		textColorRow.AddChild(_textColor);

		var imageRow = new HBoxContainer();
		imageRow.AddThemeConstantOverride("separation", 8);
		form.AddChild(imageRow);
		imageRow.AddChild(new Label { Text = "中心图：" });
		_imagePicker = new OptionButton { Name = "TokenImagePicker", CustomMinimumSize = new Vector2(260f, 0f) };
		_imagePicker.ItemSelected += idx =>
			Mutate(d => d.Image = _imagePicker.GetItemMetadata((int)idx).AsString());
		imageRow.AddChild(_imagePicker);

		var rescan = new Button { Text = "重新扫描图片" };
		rescan.Pressed += RefreshImageList;
		imageRow.AddChild(rescan);

		form.AddChild(new Label
		{
			Text = "图片从「卡牌」页导入 —— 一个存档共用一套 images/。",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		});
	}

	// ------------------------------------------------------------------ 小控件

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

	private SpinBox AddSpin(
		Control parent, string label, double min, double max, double step, System.Action<double> onChanged)
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 6);
		parent.AddChild(row);
		row.AddChild(new Label { Text = $"{label}：" });

		var spin = new SpinBox
		{
			MinValue = min,
			MaxValue = max,
			Step = step,
			CustomMinimumSize = new Vector2(110f, 0f),
		};

		spin.ValueChanged += v => onChanged(v);
		row.AddChild(spin);
		return spin;
	}

	private OptionButton AddOption<T>(
		Control parent, string label, T[] values, System.Action<T> onPicked)
		where T : struct, System.Enum
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 6);
		parent.AddChild(row);
		row.AddChild(new Label { Text = $"{label}：" });

		var picker = new OptionButton { CustomMinimumSize = new Vector2(180f, 0f) };
		for (int i = 0; i < values.Length; i++)
		{
			picker.AddItem(ShapeName(values[i]));
			picker.SetItemMetadata(picker.ItemCount - 1, System.Convert.ToInt32(values[i]));
		}

		picker.ItemSelected += idx => onPicked(values[(int)idx]);
		row.AddChild(picker);
		return picker;
	}

	private static string ShapeName<T>(T value) where T : struct, System.Enum => value switch
	{
		TokenShape.Circle => "圆形",
		TokenShape.Square => "方形",
		TokenShape.Hexagon => "六边形",
		TokenShape.Triangle => "三角形",
		_ => value.ToString(),
	};

	// ------------------------------------------------------------------ 刷新

	public override void OnShown()
	{
		RefreshList();
		RefreshImageList();
		RefreshFields();
	}

	public void RefreshList()
	{
		if (!IsInstanceValid(_list))
			return;

		// 计数放在最前面：它要如实反映"这个方法被调了几次"，
		// 而不是"它成功重建了几次"。
		ListRebuildCount++;

		foreach (Node child in _list.GetChildren())
		{
			_list.RemoveChild(child);
			child.QueueFree();
		}

		ListedIds.Clear();
		foreach (TokenDefinition def in CardDefinitionService.ListTokens(Objects))
		{
			string id = def.Id;
			ListedIds.Add(id);

			string name = string.IsNullOrWhiteSpace(def.DisplayName) ? "（无名）" : def.DisplayName;
			var button = new Button
			{
				Text = $"{name}　{id}",
				Alignment = HorizontalAlignment.Left,
				ClipText = true,
				TooltipText = id,
				ToggleMode = true,
				ButtonPressed = id == SelectedId,
				CustomMinimumSize = new Vector2(240f, 26f),
				SizeFlagsHorizontal = SizeFlags.ExpandFill,
			};

			button.Pressed += () => Select(id);
			_list.AddChild(button);
		}

		if (SelectedId.Length == 0 && ListedIds.Count > 0)
			SelectedId = ListedIds[0];

		if (!ListedIds.Contains(SelectedId))
			SelectedId = ListedIds.Count > 0 ? ListedIds[0] : "";
	}

	public void Select(string id)
	{
		SelectedId = id;
		RefreshList();
		RefreshFields();
	}

	private void RefreshFields()
	{
		if (!IsInstanceValid(_name))
			return;

		TokenDefinition? def = Selected;
		if (def is null)
		{
			_summary.Text = "还没有指示物。点「新建指示物」开始。";
			_preview.SetDefinition(null);
			return;
		}

		int usage = Objects.CountTokenInstances(def.Id);
		_summary.Text = $"「{def.DisplayName}」　桌上有 {usage} 个";

		ApplyText(_name, def.DisplayName);

		SetIfChanged(_shape, (int)def.Shape);

		SetIfChanged(_size, def.Size);

		SetIfChanged(_borderWidth, def.BorderWidth);

		_fill.Color = def.Fill;

		_border.Color = def.Border;

		// 文字框在<b>用户正在打字</b>时不许被重写（理由见 EditorPage.ApplyText）
		ApplyText(_text, def.Text);

		SetIfChanged(_fontSize, def.FontSize);
		SetIfChanged(_textOffsetY, def.TextOffsetY);
		_textColor.Color = def.TextColor;

		for (int i = 0; i < _imagePicker.ItemCount; i++)
		{
			if (_imagePicker.GetItemMetadata(i).AsString() == def.Image)
			{
				_imagePicker.Selected = i;
				break;
			}
		}

		_preview.SetDefinition(def);
	}


	private void RefreshImageList()
	{
		if (!IsInstanceValid(_imagePicker))
			return;

		string keep = Selected?.Image ?? "";
		_imagePicker.Clear();
		_imagePicker.AddItem("（不用图）");
		_imagePicker.SetItemMetadata(0, "");

		foreach (string file in ImageImport.ListImages(AppPaths.CurrentSave))
		{
			_imagePicker.AddItem(file);
			_imagePicker.SetItemMetadata(_imagePicker.ItemCount - 1, file);
		}

		for (int i = 0; i < _imagePicker.ItemCount; i++)
		{
			if (_imagePicker.GetItemMetadata(i).AsString() == keep)
			{
				_imagePicker.Selected = i;
				return;
			}
		}

		_imagePicker.Selected = 0;
	}

	// ------------------------------------------------------------------ 动作

	private void Mutate(System.Action<TokenDefinition> change)
	{
		TokenDefinition? def = Selected;
		if (def is null)
			return;

		if (!CardDefinitionService.MutateToken(Objects, def.Id, change))
		{
			Hud.Toast(CardDefinitionService.LastError);
			return;
		}

		// <b>这里不刷表单。</b>模型已经由"正在操作的那个控件"改好了，
		// 而重刷表单会把每个控件的值重写一遍 —— 那会<b>把键盘焦点与输入法候选打断</b>：
		// 症状是"在输入框里每打一个字符就失去焦点，打五个数字要点五次鼠标"。
		//
		// 用户实测报过这个 bug（Token 页与区域页中招，卡牌页因为没有这一步所以没事），
		// 根因是"改模型"与"刷界面"混在了一起。现在分开：
		// 改模型在这里，刷界面只在<b>结构真的变了</b>时做（见 OnNewPressed / OnDeletePressed）。
		//
		// 预览仍然要重画 —— 它读的是同一份定义对象，但 <c>_Draw</c> 要有人叫它。
		_preview.QueueRedraw();
		Panel.NotifyChanged();
	}

	private void OnNewPressed()
	{
		string id = CardDefinitionService.CreateToken(Objects, $"指示物{Objects.TokenDefinitions.Count + 1}");
		if (id.Length == 0)
		{
			Hud.Toast(CardDefinitionService.LastError);
			return;
		}

		SelectedId = id;
		RefreshList();
		RefreshFields();
		Panel.NotifyChanged();
		Hud.Toast("已新建指示物");
	}

	private void OnDuplicatePressed()
	{
		TokenDefinition? def = Selected;
		if (def is null)
		{
			Hud.Toast("先选一个指示物");
			return;
		}

		string sourceName = def.DisplayName;
		string id = CardDefinitionService.DuplicateToken(Objects, def.Id);
		if (id.Length == 0)
		{
			Hud.Toast(CardDefinitionService.LastError);
			return;
		}

		SelectedId = id;
		RefreshList();
		RefreshFields();
		Panel.NotifyChanged();
		Hud.Toast($"已复制「{sourceName}」");
	}

	private void OnSpawnPressed()
	{
		TokenDefinition? def = Selected;
		if (def is null)
			return;

		TokenObject created = Hud.SpawnTokenAtViewCenter(def);
		Hud.Toast($"已把「{def.DisplayName}」放到桌面（{created.Uid}）");
		Panel.NotifyChanged();
	}

	private void OnDeletePressed()
	{
		TokenDefinition? def = Selected;
		if (def is null)
			return;

		if (!CardDefinitionService.DeleteToken(Objects, def.Id))
		{
			Hud.Toast(CardDefinitionService.LastError);
			return;
		}

		Hud.Toast($"已删除指示物「{def.DisplayName}」");
		SelectedId = "";
		RefreshList();
		RefreshFields();
		Panel.NotifyChanged();
	}
}

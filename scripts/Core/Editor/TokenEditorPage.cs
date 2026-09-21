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

	/// <summary>
	/// 取场景里的控件并接线（【项目约定】禁止动态生成节点）。
	///
	/// 控件树在 <c>scenes/editor/TokenEditorPage.tscn</c> 里，
	/// <c>Preview</c> 那个节点挂的是 <see cref="TokenPreview"/> 脚本
	/// —— 它也是场景的一部分，不再在代码里 new。
	///
	/// 两处"枚举下拉"的项仍然在这里填：项**随枚举走**（<c>TokenShape</c> 加一档，
	/// 下拉要跟着多一项），写进场景的话两者会不一致。
	/// 这是"结构在场景、内容按数据填"的分界。
	/// </summary>
	internal override void Initialize()
	{
		_list = GetNode<VBoxContainer>("Row/Left/ListScroll/TokenList");
		_duplicateButton = GetNode<Button>("Row/Left/DuplicateTokenButton");
		_deleteButton = GetNode<Button>("Row/Left/DeleteTokenButton");
		_summary = GetNode<Label>("Row/Right/Summary");
		_preview = GetNode<TokenPreview>("Row/Right/Preview");

		_name = GetNode<LineEdit>("Row/Right/FormScroll/Form/NameRow/Name");
		_shape = GetNode<OptionButton>("Row/Right/FormScroll/Form/ShapeRow/Shape");
		_size = GetNode<SpinBox>("Row/Right/FormScroll/Form/SizeRow/Size");
		_borderWidth = GetNode<SpinBox>("Row/Right/FormScroll/Form/BorderWidthRow/BorderWidth");
		_fill = GetNode<ColorPickerButton>("Row/Right/FormScroll/Form/ColorRow/Fill");
		_border = GetNode<ColorPickerButton>("Row/Right/FormScroll/Form/ColorRow/Border");
		_text = GetNode<LineEdit>("Row/Right/FormScroll/Form/TextRow/Text");
		_fontSize = GetNode<SpinBox>("Row/Right/FormScroll/Form/FontSizeRow/FontSize");
		_textOffsetY = GetNode<SpinBox>("Row/Right/FormScroll/Form/TextOffsetYRow/TextOffsetY");
		_textColor = GetNode<ColorPickerButton>("Row/Right/FormScroll/Form/TextColorRow/TextColor");
		_imagePicker = GetNode<OptionButton>("Row/Right/FormScroll/Form/ImageRow/TokenImagePicker");

		foreach (TokenShape shape in System.Enum.GetValues<TokenShape>())
		{
			_shape.AddItem(ShapeName(shape));
			_shape.SetItemMetadata(_shape.ItemCount - 1, (int)shape);
		}

		// 名称 / 文字两行是"提交才算数"（TextSubmitted + FocusExited），
		// 与用户在这两个框里按回车或点走是同一条路。
		SubscribeText(_name, t =>
		{
			string trimmed = t.Trim();
			if (trimmed.Length > 0)
				Mutate(d => d.DisplayName = trimmed);
		});

		SubscribeText(_text, t => Mutate(d => d.Text = t));

		_shape.ItemSelected += idx => Mutate(d => d.Shape = (TokenShape)_shape.GetItemMetadata((int)idx).AsInt32());
		_size.ValueChanged += v => Mutate(d => d.Size = (float)v);
		_borderWidth.ValueChanged += v => Mutate(d => d.BorderWidth = (float)v);
		_fill.ColorChanged += c => Mutate(d => d.Fill = c);
		_border.ColorChanged += c => Mutate(d => d.Border = c);
		_fontSize.ValueChanged += v => Mutate(d => d.FontSize = (int)v);
		_textOffsetY.ValueChanged += v => Mutate(d => d.TextOffsetY = (float)v);
		_textColor.ColorChanged += c => Mutate(d => d.TextColor = c);
		_imagePicker.ItemSelected += idx =>
			Mutate(d => d.Image = _imagePicker.GetItemMetadata((int)idx).AsString());

		GetNode<Button>("Row/Left/NewTokenButton").Pressed += OnNewPressed;
		_duplicateButton.Pressed += OnDuplicatePressed;
		GetNode<Button>("Row/Left/SpawnButton").Pressed += OnSpawnPressed;
		_deleteButton.Pressed += OnDeletePressed;
		GetNode<Button>("Row/Right/FormScroll/Form/ImageRow/RescanButton").Pressed += RefreshImageList;
	}

	/// <summary>把一个"提交才算数"的输入框接上回调（回车 / 失焦两条都算提交）。</summary>
	private static void SubscribeText(LineEdit edit, System.Action<string> onSubmit)
	{
		edit.TextSubmitted += t => onSubmit(t);
		edit.FocusExited += () => onSubmit(edit.Text);
	}

	private static string ShapeName(TokenShape shape) => shape switch
	{
		TokenShape.Circle => "圆形",
		TokenShape.Square => "方形",
		TokenShape.Hexagon => "六边形",
		TokenShape.Triangle => "三角形",
		_ => shape.ToString(),
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

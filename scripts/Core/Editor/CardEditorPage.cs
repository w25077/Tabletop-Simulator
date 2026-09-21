using Godot;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core;

/// <summary>
/// 「卡牌」页（M5 主体）：左边卡池列表、右边全部字段、末尾一张<b>实时预览</b>。
///
/// 这一页要解决的是整件事里最费时间的环节：造一批卡、改数值、看它在桌上长什么样。
/// 所以预览不是"顺手加的装饰"——它让"改一个数字"和"看到结果"之间不需要切窗口。
///
/// 骨架阶段先只有列表与说明，控件在后续步骤里逐个接上
/// （每一步都单独跑一次自检，避免一次性写完再猜哪一块不对）。
/// </summary>
public partial class CardEditorPage : EditorPage
{
	private VBoxContainer _list = null!;
	private Label _hint = null!;
	private VBoxContainer _form = null!;
	private CardPreview _preview = null!;

	// ---- 卡池的三个动作（M5.5 第 2 条）----
	private Button _newButton = null!;
	private Button _duplicateButton = null!;
	private Button _deleteButton = null!;

	/// <summary>卡池上方那三个按钮（自检要 <c>EmitSignal(Pressed)</c>，走真实信号而不是直接调方法）。</summary>
	internal Button NewCardButton => _newButton;

	internal Button DuplicateCardButton => _duplicateButton;

	internal Button DeleteCardButton => _deleteButton;

	// ---- 基本 ----
	private LineEdit _nameEdit = null!;
	private OptionButton _faceImage = null!;
	private OptionButton _backImage = null!;
	private Label _importHint = null!;
	private ColorPickerButton _faceTint = null!;
	private ColorPickerButton _backTint = null!;
	private ColorPickerButton _borderColor = null!;

	// ---- 字段 ----
	private VBoxContainer _fieldRows = null!;
	private Label _fieldHint = null!;

	/// <summary>每行字段的控件（自检按行号读回控件里的文本，验"改字段真的落到了定义上"）。</summary>
	private readonly System.Collections.Generic.List<FieldRow> _rows = new();

	// ---- 版式 ----
	private SpinBox _titleFontSize = null!;
	private SpinBox _fieldFontSize = null!;
	private SpinBox _descriptionFontSize = null!;
	private SpinBox _padding = null!;
	private SpinBox _cornerRadius = null!;
	private SpinBox _templateBorder = null!;
	private SpinBox _shadowSize = null!;
	private SpinBox _shadowX = null!;
	private SpinBox _shadowY = null!;
	private ColorPickerButton _titleColor = null!;
	private ColorPickerButton _fieldColor = null!;
	private ColorPickerButton _descriptionColor = null!;
	private CheckBox _useNameAsTitle = null!;

	/// <summary>字段编辑器的一行 —— 一行一套控件，值回写进 <c>CardDefinition.Fields</c>。</summary>
	private sealed class FieldRow
	{
		internal required LineEdit Key { get; init; }
		internal required LineEdit Label { get; init; }
		internal required LineEdit Value { get; init; }
		internal required OptionButton Slot { get; init; }
		internal required SpinBox FontSize { get; init; }
		internal required ColorPickerButton Color { get; init; }
		internal required CheckBox ShowLabel { get; init; }
		internal required Button Remove { get; init; }
	}

	/// <summary>当前选中的卡牌 id（空 = 没选）。</summary>
	public string SelectedId { get; private set; } = "";

	/// <summary>列表里当前列出的卡牌 id，按显示次序。<b>自检靠它核对"列表与卡池一致"。</b></summary>
	internal System.Collections.Generic.List<string> ListedIds { get; } = new();

	protected override void BuildContent()
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 10);
		row.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(row);

		// ---- 左：卡池 ----
		var left = new VBoxContainer { CustomMinimumSize = new Vector2(260f, 0f) };
		left.AddThemeConstantOverride("separation", 6);
		row.AddChild(left);

		left.AddChild(new Label { Text = "卡池" });

		// <b>新建 / 复制 / 删除这一排是 M5.5 第 2 条补上的。</b>
		//
		// 服务层（<c>CardDefinitionService.CreateCard</c> / <c>DeleteCard</c>）
		// 从 M5 起就在，自检也一直在用它们 —— <b>缺的只有入口</b>。
		// 而用户报的是"根本没有新建、删除卡牌的功能"，这说明了一件事：
		// <b>入口不可发现与功能不存在，在体感上没有区别。</b>
		// （同一个模式在"区域不能删除"那条上也出现过一次，那里的删除按钮
		//   藏在页面左下角，用户同样没找到。）
		var actions = new HBoxContainer { Name = "CardActions" };
		actions.AddThemeConstantOverride("separation", 4);
		left.AddChild(actions);

		_newButton = new Button { Text = "新建卡牌", Name = "NewCardButton" };
		_newButton.Pressed += OnNewPressed;
		actions.AddChild(_newButton);

		_duplicateButton = new Button { Text = "复制这张", Name = "DuplicateCardButton" };
		_duplicateButton.Pressed += OnDuplicatePressed;
		actions.AddChild(_duplicateButton);

		_deleteButton = new Button { Text = "删除这张", Name = "DeleteCardButton" };
		_deleteButton.Pressed += OnDeletePressed;
		actions.AddChild(_deleteButton);

		var scroll = new ScrollContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		left.AddChild(scroll);

		_list = new VBoxContainer { Name = "CardList", SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_list.AddThemeConstantOverride("separation", 2);
		scroll.AddChild(_list);

		// ---- 右：预览 + 编辑表单 ----
		var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		right.AddThemeConstantOverride("separation", 6);
		row.AddChild(right);

		_preview = new CardPreview
		{
			Name = "Preview",

			// <b>宽度必须显式给。</b>它是 <c>VBoxContainer</c> 的子节点，而下面的
			// 表单滚动区设了 <c>SizeFlagsVertical = ExpandFill</c> ——
			// 于是垂直方向由表单吃掉，预览控件只拿到"最小宽度"，而它的最小宽度是 0。
			//
			// 症状是<b>预览框整个不画</b>（<c>_Draw</c> 一次都没被调用，
			// 因为 Godot 会跳过零尺寸的控件），而面板、页签、卡池列表全都正常。
			// 自检靠像素判据 + `_Draw` 计数器抓到的：`preview_draw_count = 0`。
			// **"控件存在"与"控件画了东西"是两件事。**
			CustomMinimumSize = new Vector2(420f, 260f),
		};

		right.AddChild(_preview);

		var formScroll = new ScrollContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		right.AddChild(formScroll);

		_form = new VBoxContainer { Name = "CardForm", SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_form.AddThemeConstantOverride("separation", 8);
		formScroll.AddChild(_form);

		BuildBasicSection(_form);

		_fieldRows = new VBoxContainer { Name = "FieldRows", SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_fieldRows.AddThemeConstantOverride("separation", 4);
		BuildFieldsSection(_form, _fieldRows);

		BuildTemplateSection(_form);
	}

	// ------------------------------------------------------------------ 表单：基本

	private void BuildBasicSection(Control parent)
	{
		parent.AddChild(new Label { Text = "── 基本 ──" });

		_nameEdit = AddTextRow(parent, "名称", t =>
		{
			string trimmed = t.Trim();
			if (trimmed.Length > 0)
				Mutate(d => d.DisplayName = trimmed);
		});

		// 底图 / 卡背：从存档 images/ 里选，加一个导入按钮。
		_faceImage = AddImageRow(parent, "底图", file => Mutate(d => d.FaceImage = file));
		_backImage = AddImageRow(parent, "卡背", file => Mutate(d => d.BackImage = file));

		var importRow = new HBoxContainer();
		importRow.AddThemeConstantOverride("separation", 8);
		parent.AddChild(importRow);

		var importButton = new Button { Text = "导入图片…" };
		importButton.Pressed += OpenImportDialog;
		importRow.AddChild(importButton);

		_importHint = new Label { Text = "" };
		importRow.AddChild(_importHint);

		var colorRow = new HBoxContainer();
		colorRow.AddThemeConstantOverride("separation", 8);
		parent.AddChild(colorRow);

		colorRow.AddChild(new Label { Text = "底色：" });
		_faceTint = new ColorPickerButton { CustomMinimumSize = new Vector2(110f, 26f) };
		_faceTint.ColorChanged += c => Mutate(d => d.FaceTint = c);
		colorRow.AddChild(_faceTint);

		colorRow.AddChild(new Label { Text = "卡背色：" });
		_backTint = new ColorPickerButton { CustomMinimumSize = new Vector2(110f, 26f) };
		_backTint.ColorChanged += c => Mutate(d => d.BackTint = c);
		colorRow.AddChild(_backTint);

		colorRow.AddChild(new Label { Text = "边框：" });
		_borderColor = new ColorPickerButton { CustomMinimumSize = new Vector2(110f, 26f) };
		_borderColor.ColorChanged += c => Mutate(d => d.BorderColor = c);
		colorRow.AddChild(_borderColor);
	}

	// ------------------------------------------------------------------ 表单：字段

	private void BuildFieldsSection(Control parent, VBoxContainer rows)
	{
		var header = new HBoxContainer();
		header.AddThemeConstantOverride("separation", 8);
		parent.AddChild(header);

		header.AddChild(new Label { Text = "── 卡面字段 ──" });

		var add = new Button { Text = "加一个字段" };
		add.Pressed += OnAddFieldPressed;
		header.AddChild(add);

		_fieldHint = new Label { Text = "" };
		header.AddChild(_fieldHint);

		parent.AddChild(rows);
	}

	// ------------------------------------------------------------------ 表单：模板

	private void BuildTemplateSection(Control parent)
	{
		parent.AddChild(new Label { Text = "── 版式（这套参数可以整副牌共用）──" });

		var row1 = new HBoxContainer();
		row1.AddThemeConstantOverride("separation", 8);
		parent.AddChild(row1);

		row1.AddChild(new Label { Text = "卡名字号：" });
		_titleFontSize = AddSpin(row1, 8, 120, 34, v => Mutate(d => d.Template.TitleFontSize = (int)v));

		row1.AddChild(new Label { Text = "字段字号：" });
		_fieldFontSize = AddSpin(row1, 8, 120, 26, v => Mutate(d => d.Template.FieldFontSize = (int)v));

		row1.AddChild(new Label { Text = "描述字号：" });
		_descriptionFontSize = AddSpin(row1, 8, 120, 20, v => Mutate(d => d.Template.DescriptionFontSize = (int)v));

		var row2 = new HBoxContainer();
		row2.AddThemeConstantOverride("separation", 8);
		parent.AddChild(row2);

		row2.AddChild(new Label { Text = "内边距：" });
		_padding = AddSpin(row2, 0, 0.25, 0.01, v => Mutate(d => d.Template.Padding = (float)v), 2);

		row2.AddChild(new Label { Text = "圆角：" });
		_cornerRadius = AddSpin(row2, 0, 80, 1, v => Mutate(d => d.Template.CornerRadius = (float)v));

		row2.AddChild(new Label { Text = "边框宽：" });
		_templateBorder = AddSpin(row2, 0, 20, 1, v => Mutate(d => d.Template.BorderWidth = (float)v));

		var row3 = new HBoxContainer();
		row3.AddThemeConstantOverride("separation", 8);
		parent.AddChild(row3);

		row3.AddChild(new Label { Text = "投影大小：" });
		_shadowSize = AddSpin(row3, 0, 40, 1, v => Mutate(d => d.Template.ShadowSize = (int)v));

		row3.AddChild(new Label { Text = "投影偏移 X：" });
		_shadowX = AddSpin(row3, -30, 30, 1, v =>
			Mutate(d => d.Template.ShadowOffset = new Vector2((float)v, d.Template.ShadowOffset.Y)));

		row3.AddChild(new Label { Text = "Y：" });
		_shadowY = AddSpin(row3, -30, 30, 1, v =>
			Mutate(d => d.Template.ShadowOffset = new Vector2(d.Template.ShadowOffset.X, (float)v)));

		var colorRow = new HBoxContainer();
		colorRow.AddThemeConstantOverride("separation", 8);
		parent.AddChild(colorRow);

		colorRow.AddChild(new Label { Text = "卡名色：" });
		_titleColor = new ColorPickerButton { CustomMinimumSize = new Vector2(110f, 26f) };
		_titleColor.ColorChanged += c => Mutate(d => d.Template.TitleColor = c);
		colorRow.AddChild(_titleColor);

		colorRow.AddChild(new Label { Text = "字段色：" });
		_fieldColor = new ColorPickerButton { CustomMinimumSize = new Vector2(110f, 26f) };
		_fieldColor.ColorChanged += c => Mutate(d => d.Template.FieldColor = c);
		colorRow.AddChild(_fieldColor);

		colorRow.AddChild(new Label { Text = "描述色：" });
		_descriptionColor = new ColorPickerButton { CustomMinimumSize = new Vector2(110f, 26f) };
		_descriptionColor.ColorChanged += c => Mutate(d => d.Template.DescriptionColor = c);
		colorRow.AddChild(_descriptionColor);

		var row4 = new HBoxContainer();
		row4.AddThemeConstantOverride("separation", 8);
		parent.AddChild(row4);

		_useNameAsTitle = new CheckBox { Text = "没写「卡名」字段时用卡名顶替" };
		_useNameAsTitle.Toggled += on => Mutate(d => d.Template.UseDisplayNameAsTitle = on);
		row4.AddChild(_useNameAsTitle);

		var reset = new Button { Text = "版式恢复默认" };
		reset.Pressed += OnInitTemplatePressed;
		row4.AddChild(reset);
	}

	public override void OnShown() => RefreshList();

	/// <summary>按当前卡池重建左边列表。</summary>
	public void RefreshList()
	{
		if (!IsInstanceValid(_list))
			return;

		foreach (Node child in _list.GetChildren())
		{
			_list.RemoveChild(child);
			child.QueueFree();
		}

		ListedIds.Clear();

		foreach (CardDefinition def in CardDefinitionService.ListCards(Objects))
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

		RefreshDetail();
	}

	/// <summary>选中一张卡（点列表或自检调用）。</summary>
	public void Select(string id)
	{
		SelectedId = id;
		RefreshList();
	}

	/// <summary>当前选中的定义（没选或已被删则返回 null）。</summary>
	public CardDefinition? Selected =>
		SelectedId.Length > 0 && Objects.CardDefinitions.TryGetValue(SelectedId, out CardDefinition? def)
			? def
			: null;

	/// <summary>预览框里有没有东西（自检用：确认选中真的传到了预览，而不是只改了标签）。</summary>
	public bool PreviewHasDefinition => _preview.HasDefinition;

	/// <summary>预览里正面 / 背面的<b>屏幕</b>矩形（自检按它们取样像素）。</summary>
	public Rect2 PreviewFaceOnScreen => _preview.FaceRectOnScreen();

	public Rect2 PreviewBackOnScreen => _preview.BackRectOnScreen();

	/// <summary>预览控件画了几次、最后一次算出来的矩形与面积（自检诊断用）。</summary>
	public int PreviewDrawCount => _preview.DrawCount;

	public Rect2 PreviewLastFaceRect => _preview.LastFaceRect;

	public Vector2 PreviewLastArea => _preview.LastArea;

	/// <summary>预览控件自身的矩形（屏幕坐标）与可见性。</summary>
	public Rect2 PreviewRectOnScreen => new(_preview.GetGlobalRect().Position, _preview.Size);

	public bool PreviewVisible => _preview.IsVisibleInTree();

	/// <summary>
	/// 把选中那张卡的全部字段铺进表单。
	///
	/// <b>刷新时必须把「值回填」与「触发回调」分开。</b>控件在收到新值时
	/// 会发 <c>ValueChanged</c> / <c>Toggled</c> 之类的信号，而回调会写回定义 ——
	/// 于是在"换一张卡"的时候，旧值会在回填过程中被当成"用户改的"写进新卡里。
	/// 处置：回填期间置 <see cref="_suppressCallbacks"/>，回调一律早退。
	/// </summary>
	private void RefreshDetail()
	{
		if (!IsInstanceValid(_nameEdit))
			return;

		CardDefinition? def = Selected;
		if (def is null)
		{
			_importHint.Text = Objects.CardDefinitions.Count == 0
				? "卡池是空的。"
				: "（选一张卡开始编辑）";
			_preview.SetDefinition(null);
			return;
		}

		_suppressCallbacks = true;
		try
		{
			// 名称框在"用户正在打字"时不许被重写（理由见 EditorPage.ApplyText）
			ApplyText(_nameEdit, def.DisplayName);
			_faceTint.Color = def.FaceTint;
			_backTint.Color = def.BackTint;
			_borderColor.Color = def.BorderColor;

			CardFaceTemplate tpl = def.Template;
			SetIfChanged(_titleFontSize, tpl.TitleFontSize);
			SetIfChanged(_fieldFontSize, tpl.FieldFontSize);
			SetIfChanged(_descriptionFontSize, tpl.DescriptionFontSize);
			SetIfChanged(_padding, tpl.Padding);
			SetIfChanged(_cornerRadius, tpl.CornerRadius);
			SetIfChanged(_templateBorder, tpl.BorderWidth);
			SetIfChanged(_shadowSize, tpl.ShadowSize);
			SetIfChanged(_shadowX, tpl.ShadowOffset.X);
			SetIfChanged(_shadowY, tpl.ShadowOffset.Y);
			_titleColor.Color = tpl.TitleColor;
			_fieldColor.Color = tpl.FieldColor;
			_descriptionColor.Color = tpl.DescriptionColor;
			SetIfChanged(_useNameAsTitle, tpl.UseDisplayNameAsTitle);

			RefreshImageList();
			RebuildFieldRows(def);
		}
		finally
		{
			_suppressCallbacks = false;
		}

		int usage = CardDefinitionService.CardUsage(Objects, def.Id);
		System.Collections.Generic.List<string> decks = CardDefinitionService.DecksUsingCard(Objects, def.Id);

		_fieldHint.Text = def.Fields.Count == 0
			? "（没有字段 —— 只按卡名显示）"
			: $"{def.Fields.Count} 个字段";

		_importHint.Text =
			$"桌上 {usage} 张在用　·　" +
			(decks.Count == 0 ? "不属于任何卡组" : $"在卡组：{string.Join("、", decks)}");

		_preview.SetDefinition(def);
	}

	/// <summary>按定义里的字段列表重建那一块（行数变了才调它）。</summary>
	private void RebuildFieldRows(CardDefinition def)
	{
		foreach (Node child in _fieldRows.GetChildren())
		{
			_fieldRows.RemoveChild(child);
			child.QueueFree();
		}

		_rows.Clear();

		for (int i = 0; i < def.Fields.Count; i++)
		{
			int index = i;   // 闭包捕获：不取局部副本的话每行都会改到最后一行
			CardField field = def.Fields[i];

			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 4);

			var key = new LineEdit { Text = field.Key, CustomMinimumSize = new Vector2(110f, 0f), TooltipText = "字段键（同名键可用于实例级覆盖）" };
			var label = new LineEdit { Text = field.Label, CustomMinimumSize = new Vector2(90f, 0f), TooltipText = "显示用的标签，留空则显示键" };
			var value = new LineEdit { Text = field.Value, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "字段值" };

			key.TextChanged += _ => MutateField(index, f => f.Key = key.Text);
			label.TextChanged += _ => MutateField(index, f => f.Label = label.Text);
			value.TextChanged += _ => MutateField(index, f => f.Value = value.Text);

			row.AddChild(key);
			row.AddChild(label);
			row.AddChild(value);

			var slot = new OptionButton { CustomMinimumSize = new Vector2(130f, 0f) };
			foreach (FieldSlot s in System.Enum.GetValues<FieldSlot>())
			{
				slot.AddItem(SlotName(s));
				slot.SetItemMetadata(slot.ItemCount - 1, (int)s);
			}

			slot.Selected = (int)field.Slot;
			slot.ItemSelected += idx => MutateField(index, f => f.Slot = (FieldSlot)slot.GetItemMetadata((int)idx).AsInt32());
			row.AddChild(slot);

			var fontSize = new SpinBox
			{
				MinValue = 0,
				MaxValue = 120,
				Step = 1,
				Value = field.FontSize,
				CustomMinimumSize = new Vector2(80f, 0f),
				TooltipText = "字号；0 = 用版式里的默认值",
			};

			fontSize.ValueChanged += v => MutateField(index, f => f.FontSize = (int)v);
			row.AddChild(fontSize);

			var color = new ColorPickerButton
			{
				Color = field.Color ?? def.Template.FieldColor,
				CustomMinimumSize = new Vector2(64f, 26f),
				TooltipText = "颜色",
			};

			color.ColorChanged += c => MutateField(index, f => f.Color = c);
			row.AddChild(color);

			var showLabel = new CheckBox { Text = "带标签", ButtonPressed = field.ShowLabel };
			showLabel.Toggled += on => MutateField(index, f => f.ShowLabel = on);
			row.AddChild(showLabel);

			var remove = new Button { Text = "删" };
			remove.Pressed += () => OnRemoveFieldPressed(index);
			row.AddChild(remove);

			_fieldRows.AddChild(row);
			_rows.Add(new FieldRow
			{
				Key = key, Label = label, Value = value, Slot = slot,
				FontSize = fontSize, Color = color, ShowLabel = showLabel, Remove = remove,
			});
		}
	}

	private static string SlotName(FieldSlot slot) => slot switch
	{
		FieldSlot.Title => "卡名",
		FieldSlot.TypeLine => "类型行",
		FieldSlot.TopLeft => "左上",
		FieldSlot.TopRight => "右上",
		FieldSlot.CenterLeft => "中左",
		FieldSlot.Center => "中间",
		FieldSlot.CenterRight => "中右",
		FieldSlot.BottomLeft => "左下",
		FieldSlot.BottomCenter => "中下",
		FieldSlot.BottomRight => "右下",
		FieldSlot.Description => "描述区",
		_ => slot.ToString(),
	};

	/// <summary>改某一行字段（按行号，不按引用 —— 行会被重建）。</summary>
	private void MutateField(int index, System.Action<CardField> change)
	{
		if (_suppressCallbacks)
			return;

		Mutate(d =>
		{
			if (index >= 0 && index < d.Fields.Count)
				change(d.Fields[index]);
		});
	}

	private void OnAddFieldPressed()
	{
		CardDefinition? def = Selected;
		if (def is null)
			return;

		Mutate(d => d.Fields.Add(new CardField
		{
			Key = "newField",
			Value = "值",

			// 默认给"左下"而不是"描述区"：描述区是<b>长文本</b>那一档，
			// 而新字段十有八九是个数字（费用 / 攻击 / 生命）。
			// 顺带避免一个自检假前置：原来默认就是描述区，
			// 于是"把槽位改成描述区"那条断言的前提根本不成立。
			Slot = FieldSlot.BottomLeft,
		}));

		RefreshDetail();   // 行数变了，要重建
		Hud.Toast("加了一个字段（键与值可以直接改）");
	}

	private void OnRemoveFieldPressed(int index)
	{
		CardDefinition? def = Selected;
		if (def is null || index < 0 || index >= def.Fields.Count)
			return;

		string key = def.Fields[index].Key;
		Mutate(d => d.Fields.RemoveAt(index));
		RefreshDetail();
		Hud.Toast($"删掉了字段「{key}」");
	}

	private void OnInitTemplatePressed()
	{
		CardDefinition? def = Selected;
		if (def is null)
			return;

		CardFaceTemplate fresh = new();
		Mutate(d =>
		{
			d.Template.Padding = fresh.Padding;
			d.Template.TitleFontSize = fresh.TitleFontSize;
			d.Template.FieldFontSize = fresh.FieldFontSize;
			d.Template.DescriptionFontSize = fresh.DescriptionFontSize;
			d.Template.CornerRadius = fresh.CornerRadius;
			d.Template.BorderWidth = fresh.BorderWidth;
			d.Template.TitleColor = fresh.TitleColor;
			d.Template.FieldColor = fresh.FieldColor;
			d.Template.DescriptionColor = fresh.DescriptionColor;
			d.Template.ShadowOffset = fresh.ShadowOffset;
			d.Template.ShadowSize = fresh.ShadowSize;
			d.Template.ShadowColor = fresh.ShadowColor;
			d.Template.UseDisplayNameAsTitle = fresh.UseDisplayNameAsTitle;
		});

		RefreshDetail();
		Hud.Toast("版式已恢复默认");
	}

	// ------------------------------------------------------------------ 卡池的三个动作

	/// <summary>
	/// 新建一张卡并立刻选中它。
	///
	/// 结构变了所以<b>必须显式刷列表</b>（M5 那轮定的规矩：<c>NotifyChanged</c> 只管顶栏脏标记，
	/// 谁改结构谁自己刷）。第一版把刷新全压在 <c>NotifyChanged</c> 上，
	/// 修"打字抢焦点"时把它去掉了，于是"新建了卡但列表里没有"当场变红。
	/// </summary>
	private void OnNewPressed()
	{
		string id = CardDefinitionService.CreateCard(Objects, $"新卡牌{Objects.CardDefinitions.Count + 1}");
		if (id.Length == 0)
		{
			Hud.Toast(CardDefinitionService.LastError);
			return;
		}

		SelectedId = id;
		RefreshList();
		Panel.NotifyChanged();
		Hud.Toast("已新建一张卡（在右边改它的名字与字段）");
	}

	/// <summary>复制选中那张：字段与版式都是深拷贝，改副本不污染原卡。</summary>
	private void OnDuplicatePressed()
	{
		CardDefinition? def = Selected;
		if (def is null)
		{
			Hud.Toast("先选一张卡");
			return;
		}

		string sourceName = def.DisplayName;
		string id = CardDefinitionService.DuplicateCard(Objects, def.Id);
		if (id.Length == 0)
		{
			Hud.Toast(CardDefinitionService.LastError);
			return;
		}

		SelectedId = id;
		RefreshList();
		Panel.NotifyChanged();
		Hud.Toast($"已复制「{sourceName}」");
	}

	/// <summary>
	/// 删除选中那张。
	///
	/// 桌上的实例还在就<b>拒绝</b>（闸门在 <see cref="CardDefinitionService.DeleteCard"/> 里），
	/// 被拒时把原因（"桌上还有 3 张牌在用"）原样吐在提示条上 ——
	/// 一句"删不掉"要能告诉人下一步该干什么。
	/// </summary>
	private void OnDeletePressed()
	{
		CardDefinition? def = Selected;
		if (def is null)
		{
			Hud.Toast("先选一张卡");
			return;
		}

		if (!CardDefinitionService.DeleteCard(Objects, def.Id))
		{
			Hud.Toast(CardDefinitionService.LastError);
			return;
		}

		Hud.Toast($"已删除卡牌「{def.DisplayName}」");
		SelectedId = "";
		RefreshList();
		Panel.NotifyChanged();
	}

	private void RefreshImageList()
	{
		if (!IsInstanceValid(_faceImage))
			return;

		FillImagePicker(_faceImage, Selected?.FaceImage ?? "");
		FillImagePicker(_backImage, Selected?.BackImage ?? "");
	}

	private static void FillImagePicker(OptionButton picker, string keep)
	{
		picker.Clear();
		picker.AddItem("（不用图）");
		picker.SetItemMetadata(0, "");

		foreach (string file in ImageImport.ListImages(AppPaths.CurrentSave))
		{
			picker.AddItem(file);
			picker.SetItemMetadata(picker.ItemCount - 1, file);
		}

		picker.Selected = 0;
		for (int i = 0; i < picker.ItemCount; i++)
		{
			if (picker.GetItemMetadata(i).AsString() == keep)
			{
				picker.Selected = i;
				return;
			}
		}
	}

	/// <summary>页脚那一行提示（给"导入失败"这类消息用）。</summary>
	public void SetHint(string text)
	{
		if (IsInstanceValid(_hint))
			_hint.Text = text;
	}

	// ------------------------------------------------------------------ 自检入口

	/// <summary>按行号读回"表单上显示的那个字段值"（自检用它验证回填真的发生了）。</summary>
	internal string FieldValueForTest(int row) =>
		row >= 0 && row < _rows.Count ? _rows[row].Value.Text : "";

	internal int FieldRowCount => _rows.Count;

	internal string NameForTest => _nameEdit.Text;

	// ------------------------------------------------------------------ 自检入口
	//
	// 这些方法刻意<b>只点按钮</b>（<c>EmitSignal</c>），不另写一套实现 ——
	// 自检要验的是"点这个按钮会发生什么"。写成"直接调服务层"的话，
	// 验的就只是服务层，而按钮接错线的 bug 照样溜过去。

	internal void AddFieldForTest() => OnAddFieldPressed();

	internal void RemoveFieldForTest(int index) => OnRemoveFieldPressed(index);

	/// <summary>
	/// 把第 <paramref name="row"/> 行的值填进输入框并写回定义。
	///
	/// <b>为什么不能只改 <c>Text</c>：</b><c>LineEdit.Text = "…"</c> <b>不会</b>触发
	/// <c>TextChanged</c>（那个信号是给"用户输入"用的；程序化赋值虽然文档说会发，
	/// 但世界里的行为是不发 —— 自检第一次跑就是红的）。
	/// 所以这里显式再调一次回调。**用户真的打字时走的是另一条路**（信号），
	/// 而那条路由 <see cref="SetFieldSlotForTest"/> 那种"走控件的信号"的入口来验。
	/// </summary>
	internal void SetFieldValueForTest(int row, string value)
	{
		if (row < 0 || row >= _rows.Count)
			return;

		_rows[row].Value.Text = value;
		int index = row;
		MutateField(index, f => f.Value = value);
	}

	internal void SetFieldSlotForTest(int row, FieldSlot slot)
	{
		if (row < 0 || row >= _rows.Count)
			return;

		// 走控件的信号，与用户在选择框里点一下是同一条路
		_rows[row].Slot.Selected = (int)slot;
		_rows[row].Slot.EmitSignal(OptionButton.SignalName.ItemSelected, (int)slot);
	}

	internal void SetTitleFontSizeForTest(int size)
	{
		_titleFontSize.Value = size;
		Mutate(d => d.Template.TitleFontSize = size);
	}

	internal void SetPaddingForTest(float padding)
	{
		_padding.Value = padding;
		Mutate(d => d.Template.Padding = padding);
	}

	/// <summary>
	/// 回填表单期间置真：此时控件的 <c>ValueChanged</c> / <c>TextChanged</c> 一律早退。
	///
	/// <b>没有它就会出现"改 A 卡、B 卡被改了"。</b>换一张卡时我们要把新值写进控件，
	/// 而控件收到新值会发信号 → 回调写回定义 —— 那一刻"当前选中的"已经换了人，
	/// 于是旧卡的值被写进新卡。这类 bug 只在"连着点两张卡"时出现，肉眼极难归因。
	/// </summary>
	private bool _suppressCallbacks;

	// ------------------------------------------------------------------ 控件工厂

	private LineEdit AddTextRow(Control parent, string label, System.Action<string> onSubmit)
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 8);
		parent.AddChild(row);
		row.AddChild(new Label { Text = $"{label}：" });

		var edit = new LineEdit
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(200f, 0f),
		};

		edit.TextSubmitted += t => onSubmit(t);
		edit.FocusExited += () => onSubmit(edit.Text);
		row.AddChild(edit);
		return edit;
	}

	private OptionButton AddImageRow(Control parent, string label, System.Action<string> onPicked)
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 8);
		parent.AddChild(row);
		row.AddChild(new Label { Text = $"{label}：" });

		var picker = new OptionButton { CustomMinimumSize = new Vector2(260f, 0f), SizeFlagsHorizontal = SizeFlags.ExpandFill };
		picker.ItemSelected += idx =>
		{
			if (_suppressCallbacks)
				return;

			onPicked(picker.GetItemMetadata((int)idx).AsString());
		};

		row.AddChild(picker);
		return picker;
	}

	/// <summary>只放一个数字框（不自带标签 —— 版式那一区块要几个框挤在一行）。</summary>
	private SpinBox AddSpin(
		Control parent, double min, double max, double initial, System.Action<double> onChanged, int decimals = 0)
	{
		var spin = new SpinBox
		{
			MinValue = min,
			MaxValue = max,
			Step = decimals > 0 ? 0.01 : 1,
			Value = initial,
			CustomMinimumSize = new Vector2(decimals > 0 ? 80f : 70f, 0f),
			Rounded = decimals == 0,
		};

		spin.ValueChanged += v =>
		{
			if (!_suppressCallbacks)
				onChanged(v);
		};

		parent.AddChild(spin);
		return spin;
	}

	// ------------------------------------------------------------------ 改定义

	/// <summary>
	/// 改选中那张卡的一处，并<b>立刻推给桌面与预览</b>。
	///
	/// 走 <see cref="CardDefinitionService.MutateCard"/>：它改定义、把新定义交给场上每一张卡、
	/// 发信号、标脏。编辑器不自己遍历场上实例 —— 那是物件系统的知识
	/// （哪些卡在用这份定义、实例级覆盖要不要保留）。
	/// </summary>
	private void Mutate(System.Action<CardDefinition> change)
	{
		if (_suppressCallbacks)
			return;

		CardDefinition? def = Selected;
		if (def is null)
			return;

		if (!CardDefinitionService.MutateCard(Objects, def.Id, change))
		{
			Hud.Toast(CardDefinitionService.LastError);
			return;
		}

		// 预览持有的是同一份定义对象，但 <c>_Draw</c> 要有人叫它重画。
		_preview.QueueRedraw();

		// 注意<b>不在这里调 RefreshDetail()</b>：那会把控件里的文本重新写一遍，
		// 于是正在输入的那个框会跳到末尾、光标丢失。只有"行数变了"才重建
		// （见 OnAddFieldPressed / OnRemoveFieldPressed）。
		Panel.NotifyChanged();
	}

	// ------------------------------------------------------------------ 导入图片

	/// <summary>
	/// 弹系统文件选择框导入一张图。
	///
	/// 真正的拷贝逻辑在 <see cref="ImageImport.Copy"/>（纯 <c>FileAccess</c>，可自检），
	/// 这里只负责"拿到一个路径" + 把结果接到定义上。
	/// 导进来的图会**立刻**被选为当前卡的底图 —— 导完还要再去下拉里找一遍是多余动作。
	/// </summary>
	private void OpenImportDialog()
	{
		if (Selected is null)
		{
			Hud.Toast("先选一张卡");
			return;
		}

		FileDialog dialog = new()
		{
			FileMode = FileDialog.FileModeEnum.OpenFile,
			Access = FileDialog.AccessEnum.Filesystem,
			Title = "选一张图片（会复制进当前存档的 images/）",
			UseNativeDialog = true,
		};

		dialog.AddFilter("*.png,*.jpg,*.jpeg,*.webp,*.bmp", "图片");
		dialog.FileSelected += OnImportFileSelected;

		// 挂在面板自己下面：它是 <c>Window</c>，用完要 <c>QueueFree</c>，
		// 否则每点一次导入就积一个（`PopupMenu` 那一类坑的亲戚）。
		AddChild(dialog);
		dialog.PopupCentered(new Vector2I(900, 600));
	}

	private void OnImportFileSelected(string path)
	{
		string fileName = ImageImport.Copy(AppPaths.CurrentSave, path);
		if (fileName.Length == 0)
		{
			Hud.Toast($"导入失败：{ImageImport.LastError}");
			SetHint($"导入失败：{ImageImport.LastError}");
			return;
		}

		Mutate(d => d.FaceImage = fileName);

		// 新导入的图要出现在两个下拉里（底图 / 卡背），并且选中它
		RefreshDetail();
		Hud.Toast($"已导入 {fileName} 并设为底图");
	}

	/// <summary>导入对话框里的那个 <c>Window</c> 用完就收（自检也会用到这个约定）。</summary>
	internal void CloseImportDialogs()
	{
		foreach (Node child in GetChildren())
		{
			if (child is FileDialog dialog)
			{
				dialog.Hide();
				RemoveChild(dialog);
				dialog.QueueFree();
			}
		}
	}
}

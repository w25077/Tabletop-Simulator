using Godot;

namespace TabletopSimulator.Core;

/// <summary>
/// 「桌面」页：背景图 / 底色 / 桌面尺寸 / 网格。改完<b>立刻生效</b>。
///
/// 这一页只有一个容易做错的地方：<b>它改的是 <see cref="Board.Theme"/> 那个活对象</b>，
/// 所以每次都要走 <see cref="Board.ApplyTheme"/>（它会重载背景图并重绘）。
/// 直接改 <c>Theme</c> 上的字段不会重画 —— 表现为"填了数字，桌面没动"。
/// </summary>
public partial class BoardEditorPage : EditorPage
{
	private LineEdit _width = null!;
	private LineEdit _height = null!;
	private SpinBox _gridSize = null!;
	private SpinBox _majorEvery = null!;
	private ColorPickerButton _background = null!;
	private ColorPickerButton _border = null!;
	private CheckBox _showGrid = null!;
	private CheckBox _showBounds = null!;
	private CheckBox _tileBackground = null!;
	private OptionButton _backgroundImage = null!;
	private Label _imageHint = null!;

	protected override void BuildContent()
	{
		var scroll = new ScrollContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		AddChild(scroll);

		var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		column.AddThemeConstantOverride("separation", 8);
		scroll.AddChild(column);

		// ---- 背景 ----
		column.AddChild(new Label { Text = "背景" });

		var bgRow = new HBoxContainer();
		bgRow.AddThemeConstantOverride("separation", 8);
		column.AddChild(bgRow);

		bgRow.AddChild(new Label { Text = "背景图：" });
		_backgroundImage = new OptionButton { CustomMinimumSize = new Vector2(280f, 0f) };
		_backgroundImage.ItemSelected += _ => ApplyImage();
		bgRow.AddChild(_backgroundImage);

		var rescan = new Button { Text = "重新扫描图片" };
		rescan.Pressed += RefreshImageList;
		bgRow.AddChild(rescan);

		_imageHint = new Label { Text = "" };
		bgRow.AddChild(_imageHint);

		var colorRow = new HBoxContainer();
		colorRow.AddThemeConstantOverride("separation", 8);
		column.AddChild(colorRow);

		colorRow.AddChild(new Label { Text = "底色：" });
		_background = new ColorPickerButton { CustomMinimumSize = new Vector2(120f, 28f) };
		_background.ColorChanged += _ => Mutate(theme => theme.BackgroundColor = _background.Color);
		colorRow.AddChild(_background);

		_tileBackground = new CheckBox { Text = "平铺（不勾 = 拉伸铺满）" };
		_tileBackground.Toggled += on => Mutate(theme => theme.BackgroundTile = on);
		colorRow.AddChild(_tileBackground);

		// ---- 尺寸 ----
		column.AddChild(new Label { Text = "桌面尺寸（世界坐标，左上角固定在原点）" });

		var sizeRow = new HBoxContainer();
		sizeRow.AddThemeConstantOverride("separation", 8);
		column.AddChild(sizeRow);

		sizeRow.AddChild(new Label { Text = "宽：" });
		_width = new LineEdit { CustomMinimumSize = new Vector2(110f, 0f) };
		_width.TextSubmitted += _ => ApplySize();
		sizeRow.AddChild(_width);

		sizeRow.AddChild(new Label { Text = "高：" });
		_height = new LineEdit { CustomMinimumSize = new Vector2(110f, 0f) };
		_height.TextSubmitted += _ => ApplySize();
		sizeRow.AddChild(_height);

		var applySize = new Button { Text = "应用尺寸" };
		applySize.Pressed += ApplySize;
		sizeRow.AddChild(applySize);

		var fit = new Button { Text = "把整桌装进视野" };
		fit.Pressed += () => Camera.FocusOnRect(Board.BoardRect, 40f, 1f);
		sizeRow.AddChild(fit);

		// ---- 网格 ----
		column.AddChild(new Label { Text = "网格" });

		var gridRow = new HBoxContainer();
		gridRow.AddThemeConstantOverride("separation", 8);
		column.AddChild(gridRow);

		_showGrid = new CheckBox { Text = "显示网格" };
		_showGrid.Toggled += on => Mutate(theme => theme.ShowGrid = on);
		gridRow.AddChild(_showGrid);

		gridRow.AddChild(new Label { Text = "格宽：" });
		_gridSize = new SpinBox { MinValue = 10, MaxValue = 2000, Step = 10, CustomMinimumSize = new Vector2(100f, 0f) };
		_gridSize.ValueChanged += v => Mutate(theme => theme.GridSize = (int)v);
		gridRow.AddChild(_gridSize);

		gridRow.AddChild(new Label { Text = "每几格一条粗线：" });
		_majorEvery = new SpinBox { MinValue = 1, MaxValue = 20, Step = 1, CustomMinimumSize = new Vector2(90f, 0f) };
		_majorEvery.ValueChanged += v => Mutate(theme => theme.MajorGridEvery = (int)v);
		gridRow.AddChild(_majorEvery);

		// ---- 边界 ----
		var boundsRow = new HBoxContainer();
		boundsRow.AddThemeConstantOverride("separation", 8);
		column.AddChild(boundsRow);

		_showBounds = new CheckBox { Text = "显示桌面边界" };
		_showBounds.Toggled += on => Mutate(theme => theme.ShowBoardBounds = on);
		boundsRow.AddChild(_showBounds);

		boundsRow.AddChild(new Label { Text = "边界色：" });
		_border = new ColorPickerButton { CustomMinimumSize = new Vector2(120f, 28f) };
		_border.ColorChanged += _ => Mutate(theme => theme.BorderColor = _border.Color);
		boundsRow.AddChild(_border);

		var reset = new Button { Text = "恢复默认桌面" };
		reset.Pressed += OnResetPressed;
		column.AddChild(reset);
	}

	public override void OnShown()
	{
		RefreshFields();
		RefreshImageList();
	}

	private void RefreshFields()
	{
		if (!IsInstanceValid(_width))
			return;

		BoardTheme theme = Board.Theme;
		ApplyText(_width, theme.BoardWidth.ToString("0"));
		ApplyText(_height, theme.BoardHeight.ToString("0"));
		SetIfChanged(_gridSize, theme.GridSize);
		SetIfChanged(_majorEvery, theme.MajorGridEvery);
		SetIfChanged(_showGrid, theme.ShowGrid);
		SetIfChanged(_showBounds, theme.ShowBoardBounds);
		SetIfChanged(_tileBackground, theme.BackgroundTile);
		_background.Color = theme.BackgroundColor;
		_border.Color = theme.BorderColor;
	}

	private void RefreshImageList()
	{
		if (!IsInstanceValid(_backgroundImage))
			return;

		string keep = Board.Theme.BackgroundImage;
		_backgroundImage.Clear();
		_backgroundImage.AddItem("（只用底色）");
		_backgroundImage.SetItemMetadata(0, "");

		foreach (string file in ImageImport.ListImages(AppPaths.CurrentSave))
		{
			_backgroundImage.AddItem(file);
			_backgroundImage.SetItemMetadata(_backgroundImage.ItemCount - 1, file);
		}

		for (int i = 0; i < _backgroundImage.ItemCount; i++)
		{
			if (_backgroundImage.GetItemMetadata(i).AsString() == keep)
			{
				_backgroundImage.Selected = i;
				break;
			}
		}

		_imageHint.Text = _backgroundImage.ItemCount <= 1
			? "（存档里还没有图片 —— 用「卡牌」页的「导入图片」）"
			: "";
	}

	// ------------------------------------------------------------------ 应用

	/// <summary>
	/// 改一处主题并立刻生效。
	///
	/// <b>每个控件都必须走这里</b>，不许直接改 <c>Board.Theme</c> 的字段：
	/// 那样不会重绘（症状是"填了数字，桌面没动"），而背景图那一路还会
	/// 因为没重载纹理而一直显示旧图。
	/// </summary>
	private void Mutate(System.Action<BoardTheme> change)
	{
		change(Board.Theme);
		Board.ApplyTheme(Board.Theme);
		Panel.NotifyChanged();
	}

	private void ApplyImage()
	{
		string file = _backgroundImage.GetItemMetadata(_backgroundImage.Selected).AsString();
		Mutate(theme => theme.BackgroundImage = file);
		Hud.Toast(file.Length == 0 ? "桌面背景已清空" : $"桌面背景：{file}");
	}

	private void ApplySize()
	{
		if (!float.TryParse(_width.Text, out float w) || !float.TryParse(_height.Text, out float h))
		{
			Hud.Toast("宽高要填数字");
			return;
		}

		if (w < 200f || h < 200f)
		{
			Hud.Toast("桌面太小了（最小 200×200）");
			return;
		}

		Mutate(theme =>
		{
			theme.BoardWidth = w;
			theme.BoardHeight = h;
		});

		RefreshFields();
		Hud.Toast($"桌面尺寸：{w:0} × {h:0}");
	}

	private void OnResetPressed()
	{
		BoardTheme fresh = new();
		Mutate(theme =>
		{
			theme.BackgroundImage = fresh.BackgroundImage;
			theme.BackgroundColor = fresh.BackgroundColor;
			theme.BackgroundTile = fresh.BackgroundTile;
			theme.BoardWidth = fresh.BoardWidth;
			theme.BoardHeight = fresh.BoardHeight;
			theme.ShowBoardBounds = fresh.ShowBoardBounds;
			theme.BorderColor = fresh.BorderColor;
			theme.ShowGrid = fresh.ShowGrid;
			theme.GridSize = fresh.GridSize;
			theme.MajorGridEvery = fresh.MajorGridEvery;
			theme.GridColor = fresh.GridColor;
			theme.MajorGridColor = fresh.MajorGridColor;
		});

		RefreshFields();
		RefreshImageList();
		Hud.Toast("桌面已恢复默认");
	}

	// ------------------------------------------------------------------ 自检入口

	/// <summary>
	/// 按"文本框里填了宽高然后点应用"那条路改尺寸。
	///
	/// 刻意<b>不把校验逻辑抽成另一份"给测试用"的实现</b>：自检走的就是
	/// 那个按钮按下时执行的同一段代码（<see cref="ApplySize"/>），
	/// 只是先用代码把两个输入框填好。抽一份的话，验的是那份替身。
	/// </summary>
	internal void ApplySizeForTest(float width, float height)
	{
		_width.Text = width.ToString("0");
		_height.Text = height.ToString("0");
		ApplySize();
	}

	internal void SetGridSizeForTest(int size)
	{
		_gridSize.Value = size;
		Mutate(theme => theme.GridSize = size);
	}
}

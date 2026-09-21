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

	/// <summary>
	/// 取场景里的控件并接线（【项目约定】禁止动态生成节点）。
	///
	/// 控件树在 <c>scenes/editor/BoardEditorPage.tscn</c> 里 ——
	/// 在编辑器里打开它就能看到这一页长什么样、每个控件的属性是什么，
	/// 改布局不用编译、不用截图。
	/// </summary>
	internal override void Initialize()
	{
		_backgroundImage = GetNode<OptionButton>("Scroll/Column/BgRow/BackgroundImage");
		_imageHint = GetNode<Label>("Scroll/Column/BgRow/ImageHint");
		_background = GetNode<ColorPickerButton>("Scroll/Column/ColorRow/Background");
		_tileBackground = GetNode<CheckBox>("Scroll/Column/ColorRow/TileBackground");
		_width = GetNode<LineEdit>("Scroll/Column/SizeRow/Width");
		_height = GetNode<LineEdit>("Scroll/Column/SizeRow/Height");
		_showGrid = GetNode<CheckBox>("Scroll/Column/GridRow/ShowGrid");
		_gridSize = GetNode<SpinBox>("Scroll/Column/GridRow/GridSize");
		_majorEvery = GetNode<SpinBox>("Scroll/Column/GridRow/MajorEvery");
		_showBounds = GetNode<CheckBox>("Scroll/Column/BoundsRow/ShowBounds");
		_border = GetNode<ColorPickerButton>("Scroll/Column/BoundsRow/Border");

		_backgroundImage.ItemSelected += _ => ApplyImage();
		GetNode<Button>("Scroll/Column/BgRow/RescanButton").Pressed += RefreshImageList;
		_background.ColorChanged += _ => Mutate(theme => theme.BackgroundColor = _background.Color);
		_tileBackground.Toggled += on => Mutate(theme => theme.BackgroundTile = on);
		_width.TextSubmitted += _ => ApplySize();
		_height.TextSubmitted += _ => ApplySize();
		GetNode<Button>("Scroll/Column/SizeRow/ApplySizeButton").Pressed += ApplySize;
		GetNode<Button>("Scroll/Column/SizeRow/FitButton").Pressed +=
			() => Camera.FocusOnRect(Board.BoardRect, 40f, 1f);
		_showGrid.Toggled += on => Mutate(theme => theme.ShowGrid = on);
		_gridSize.ValueChanged += v => Mutate(theme => theme.GridSize = (int)v);
		_majorEvery.ValueChanged += v => Mutate(theme => theme.MajorGridEvery = (int)v);
		_showBounds.Toggled += on => Mutate(theme => theme.ShowBoardBounds = on);
		_border.ColorChanged += _ => Mutate(theme => theme.BorderColor = _border.Color);
		GetNode<Button>("Scroll/Column/ResetButton").Pressed += OnResetPressed;
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

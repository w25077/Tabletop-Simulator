using Godot;
using TabletopSimulator.Core.Objects;

namespace TabletopSimulator.Core;

/// <summary>
/// 编辑器内一页的基类：拿到共用依赖，并在"被切到前台"时刷新自己。
///
/// 存在的理由很朴素：四页都要「物件管理器 / 区域系统 / 桌面 / 相机 / 提示条 / 面板」
/// 这六样东西，而每一页各自 <c>GetNode</c> 去找它们，就等于有四处各自记得
/// "面板挂在哪一层"。依赖由 <see cref="EditorPanel"/> 统一注入一次。
/// </summary>
public abstract partial class EditorPage : Control
{
	protected ObjectManager Objects = null!;
	protected ZoneManager Zones = null!;
	protected Board Board = null!;
	protected BoardCamera Camera = null!;
	protected Hud Hud = null!;
	protected EditorPanel Panel = null!;

	/// <summary>由 <see cref="EditorPanel"/> 在加进 <c>TabContainer</c> 之前调用。</summary>
	internal void Bind(
		ObjectManager objects, ZoneManager zones, Board board,
		BoardCamera camera, Hud hud, EditorPanel panel)
	{
		Objects = objects;
		Zones = zones;
		Board = board;
		Camera = camera;
		Hud = hud;
		Panel = panel;
	}

	public override void _Ready()
	{
		// 分页内容撑满 TabContainer 给的那块地方。
		// 注意设 anchors 的时机：这里已经在树里了（Bind 之后才 AddChild），
		// 所以锚点会被正常解析（M4 的"0 高度面板"就是这个顺序搞反了）。
		SetAnchorsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Pass;

		// <b>这一层 <c>_Ready</c> 里只做"撑满"这件事。</b>
		//
		// 界面的其余部分（控件树）现在<b>写在 .tscn 里</b>（每个分页一个
		// <c>scenes/editor/*.tscn</c>），子类只在自己的 <c>_Ready</c> 里
		// <c>GetNode</c> 取出来接线 —— 【项目约定】禁止动态生成节点。
		//
		// <b>刻意不在这里调 <c>OnShown()</c>。</b>
		//
		// Godot 是"子节点先 _Ready"，所以这一行会在 <c>EditorPanel.Build()</c>
		// 还没跑完的时候执行 —— 而 <c>OnShown</c> 链上有一个会去读面板页脚控件的
		// <c>RefreshStatus</c>，那时那些控件<b>还不存在</b>（或者面板的依赖还没注入）。
		// 症状是一开机就刷 7 条 <c>NullReferenceException</c> 到 stderr，
		// 而面板"看起来"完全正常、自检也全绿 —— 因为它发生在建界面的过程中，
		// 没有任何一条断言会走到那里。
		//
		// 所以刷新由 <see cref="EditorPanel"/> 在整棵树装配完之后统一发起。
	}

	/// <summary>
	/// 取场景里的控件、接线。
	///
	/// 由 <see cref="EditorPanel"/> 在装配完之后对每一页调一次 ——
	/// <b>而不是各页自己在 <c>_Ready</c> 里做</b>：面板的依赖（物件系统 / 相机 / HUD）
	/// 是 <c>Main</c> 注入的，而各页的 <c>_Ready</c> 跑在那之前。
	///
	/// <b>它是抽象的，没有默认实现。</b>五个分页全部搬进
	/// <c>scenes/editor/*.tscn</c> 之后，"忘了搬"这件事不该再有回退路径可以藏身 ——
	/// 新加一页就必须实现它，否则编译不过。
	/// </summary>
	internal abstract void Initialize();

	/// <summary>被切到前台 / 数据变了：按当前数据重刷列表与预览。</summary>
	public abstract void OnShown();

	// ------------------------------------------------------------------ 别抢用户的焦点

	/// <summary>
	/// 回填一个文本输入框 —— 该不该写、由它决定，<b>不需要时就一个字都不碰</b>。
	///
	/// <b>为什么必须是"方法"而不是"返回一个值"：</b>第一版写的是
	/// <c>edit.Text = KeepEditingText(edit, value)</c>，而那个三元表达式
	/// <b>永远会执行那次赋值</b> —— 用户正在输入时它返回 <c>edit.Text</c>，
	/// 于是变成 <c>edit.Text = edit.Text</c>，而<b>把同一个字符串再赋给
	/// <c>LineEdit.Text</c> 也会清掉选中区、打断输入法候选</b>。
	///
	/// 所以判据必须是"<b>值真的不同才写</b>"，而且焦点还在时一律不写：
	/// <list type="number">
	/// <item><b>值相同 → 不写。</b>这是那个 bug 的真身：<c>RefreshFields</c>
	///   每敲一个字符把每个框重写一遍，值明明没变，选中区却没了 ——
	///   症状就是"每打一个字符失去一次焦点，打五个数字要点五次鼠标"。</item>
	/// <item><b>用户正在这个框里打字 → 不写。</b>值本来就等于用户刚敲进去的东西
	///   （模型是<b>被</b>这个输入框改的），跳过不会让界面与模型不一致；
	///   而硬写会把光标弹到末尾、把中文输入法打到一半的拼音打断。</item>
	/// </list>
	/// </summary>
	protected static void ApplyText(LineEdit edit, string value)
	{
		if (edit.Text == value || edit.HasFocus())
			return;

		edit.Text = value;
	}

	/// <summary>
	/// 只在数值<b>真的变了</b>时才写回控件。
	///
	/// 与 <see cref="KeepEditingText"/> 是一对：光挡住文本输入框还不够 ——
	/// 表单里那些 <c>SpinBox</c> / <c>CheckBox</c> 被"重写同一个值"时会触发
	/// 重排与最小尺寸重算，而<b>重排会顺带把焦点从别的控件上摘下来</b>
	/// （实测：选中一段文字、再让表单刷一次，选中区就没了 —— 而那个值压根没变）。
	///
	/// 判据用"值不同才写"，比"这个控件有没有焦点"更稳：它同时挡住了
	/// "没必要的工作"与"没必要的副作用"。
	/// </summary>
	protected static void SetIfChanged(Range control, double value)
	{
		if (!Mathf.IsEqualApprox(control.Value, value))
			control.Value = value;
	}

	/// <summary>同理：只在选项真的变了时才切（切选项会发 <c>ItemSelected</c>）。</summary>
	protected static void SetIfChanged(OptionButton control, int index)
	{
		if (control.Selected != index)
			control.Selected = index;
	}

	protected static void SetIfChanged(CheckBox control, bool pressed)
	{
		if (control.ButtonPressed != pressed)
			control.ButtonPressed = pressed;
	}

	// ------------------------------------------------------------------ 导入图片

	/// <summary>
	/// 弹系统文件选择框，把一张图拷进当前存档的 <c>images/</c>，然后交给
	/// <paramref name="onImported"/> <b>立刻套用到本页正在改的那个槽位</b>。
	///
	/// <b>为什么收到基类里：</b>原来这条路只在「卡牌」页有，于是「桌面」页的提示
	/// 干脆写着"用「卡牌」页的「导入图片」"—— 用户只想换一张桌面背景，
	/// 得先跑去另一页导图、再跑回来在下拉里找那个文件名。
	/// 「指示物」页同样只能从已有图里挑。<b>三页共用一份逻辑</b>之后，
	/// 每页各自"导完自动套用到自己那个槽位"（底图 / 背景 / 中心图），
	/// 而 <see cref="ImageImport.Copy"/> 的拷贝语义（重名不覆盖、同图复用）只有一处实现。
	///
	/// <b>对话框用完必须收。</b>它是 <c>Window</c>，留着会积攒，
	/// 而 <c>PopupMenu</c> 那一类"<c>Window</c> 抢走后续合成鼠标事件"的坑项目里已经踩过
	/// （M3 两个菜单）。所以每次弹之前先 <see cref="CloseImportDialogs"/>。
	/// </summary>
	/// <param name="onImported">参数是存档内的文件名（要写进定义的就是它）。</param>
	internal void ImportImage(System.Action<string> onImported)
	{
		CloseImportDialogs();

		FileDialog dialog = new()
		{
			FileMode = FileDialog.FileModeEnum.OpenFile,
			Access = FileDialog.AccessEnum.Filesystem,
			Title = "选一张图片（会复制进当前存档的 images/）",
			UseNativeDialog = true,
		};

		dialog.AddFilter("*.png,*.jpg,*.jpeg,*.webp,*.bmp", "图片");
		dialog.FileSelected += path => OnImportFileSelected(path, onImported);

		AddChild(dialog);
		dialog.PopupCentered(new Vector2I(900, 600));
	}

	/// <summary>
	/// 选完文件之后的处置：拷贝 → 成功就套用，失败就报错。
	///
	/// <b>它不碰界面</b>（除了吐司与提示条）—— 刷新由各页在自己的回调里做，
	/// 因为"要不要重建下拉、要不要重建列表"各页不同
	/// （M5 那轮定的规矩：谁改了结构谁自己刷）。
	/// 自检也直接调它：走的是"用户选完文件"之后的同一段代码，
	/// 而不是另写一份"给测试用"的替身。
	/// </summary>
	internal void OnImportFileSelected(string sourcePath, System.Action<string> onImported)
	{
		string fileName = ImageImport.Copy(AppPaths.CurrentSave, sourcePath);
		if (fileName.Length == 0)
		{
			Hud.Toast($"导入失败：{ImageImport.LastError}");
			SetHint($"导入失败：{ImageImport.LastError}");
			return;
		}

		onImported(fileName);
	}

	/// <summary>
	/// 自检用：把本页里所有控件的焦点摘掉。
	///
	/// 为什么需要它：<b>有焦点的控件会先把按键吃掉</b>，而 <c>LineEdit</c> 还自带文本撤销 ——
	/// 于是"在字段表里按 Ctrl+Z"退的是文本框里的字，而不是编辑器改过的定义。
	/// 探针要验的是后者，就得先把焦点摘干净（那也正是用户点一下空白处会发生的事）。
	/// </summary>
	internal void ReleaseFocusForTest()
	{
		Viewport? viewport = GetViewport();
		viewport?.GuiReleaseFocus();
	}

	/// <summary>本页当前有没有控件握着键盘焦点（诊断用：读出来，别猜）。</summary>
	internal bool HasFocusedControlForTest() => GetViewport()?.GuiGetFocusOwner() is not null;

	/// <summary>本页的提示条（各页在场景里都有，位置不同，所以由各页提供）。</summary>
	protected virtual void SetHint(string message)
	{
	}

	/// <summary>
	/// 收掉本页弹过的导入对话框。它是 <c>Window</c>，留着会积攒，
	/// 而自检更在意另一点：<b>残留的 <c>Window</c> 会抢走后续合成鼠标事件</b>。
	/// </summary>
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

	/// <summary>本页当前有几个导入对话框还挂着（自检核对"探针没留下东西"）。</summary>
	internal int ImportDialogCountForTest()
	{
		int n = 0;
		foreach (Node child in GetChildren())
		{
			if (child is FileDialog)
				n++;
		}

		return n;
	}

	/// <summary>
	/// 自检用：像用户那样<b>按下</b>本页的「导入图片…」按钮，并按"弹出了对话框"判定成败。
	///
	/// <b>存在的理由是一条实测出来的假绿：</b>第一版断言只读了按钮的文字与尺寸，
	/// 然后直接调 <c>ApplyImportedImage</c> —— 于是"按钮的 <c>Pressed</c> 接没接线"
	/// 这件事<b>根本没有裁判</b>：把那行接线注释掉，整个 <c>editor_simulation</c> 照样全绿。
	///
	/// 判据不是"信号发出去了"（<c>EmitSignal</c> 永远成功，发出去没人听也算成功），
	/// 而是<b>按下去真的多了一个对话框</b> —— 那才是"接线在"的证据。
	///
	/// 发信号而不是调 <c>OnImportPressed</c>：前者走的是"按钮被按下"那条真实路径
	/// （含接线本身），后者只是调用一个恰好叫这个名字的方法。
	/// </summary>
	internal bool PressImportButtonForTest()
	{
		Button? button = FindChild("ImportButton", true, false) as Button;
		if (button is null)
			return false;

		int before = ImportDialogCountForTest();
		button.EmitSignal(BaseButton.SignalName.Pressed);
		bool appeared = ImportDialogCountForTest() > before;

		CloseImportDialogs();
		return appeared;
	}
}

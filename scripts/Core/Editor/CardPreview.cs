using Godot;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core;

/// <summary>
/// 卡面预览：把 <see cref="CardFaceRenderer"/> 画的卡面直接搬到编辑器里。
///
/// <b>为什么能这么省事：</b>卡面渲染从一开始就是"静态纯函数 + <c>_Draw</c>"
/// （<c>CardFaceRenderer.DrawFace(CanvasItem, CardDefinition, Rect2)</c>），
/// 而不是搭一棵 <c>Control</c> 节点树。于是预览只是<b>换一个 <c>CanvasItem</c> 再画一遍</b>——
/// 桌上那张卡的画法与预览框里的画法来自同一个函数，不可能出现
/// "预览好看、桌上不一样"这种最让人不信任编辑器的症状。
///
/// 正反两面同时画（上面是正面）：卡背也是要调的东西（卡背图 / 卡背底色），
/// 而它只在盖放的牌上看得出来 —— 不放在预览里，改卡背就只能靠"去桌上翻一张牌看看"。
/// </summary>
public partial class CardPreview : Control
{
	public CardDefinition? _definition;

	/// <summary>
	/// <c>_Draw</c> 被调用了几次，以及最后一次算出来的矩形（自检用）。
	///
	/// 存在的理由与 M4 的 <c>PushTrace</c> 一样：<b>把猜换成读。</b>
	/// "预览是空的"有三种完全不同的原因 —— 控件没画过、画了但矩形为 0、
	/// 画了但被别的东西盖住 —— 光看快照像素分不出来。
	/// </summary>
	public int DrawCount { get; private set; }

	public Rect2 LastFaceRect { get; private set; }

	public Vector2 LastArea { get; private set; }

	/// <summary>正面是否画出来（自检用：确认预览真的拿到定义了）。</summary>
	public bool HasDefinition => _definition is not null;

	/// <summary>预览里正面的矩形（自检按它取样像素）。</summary>
	public Rect2 FaceRectForTest => FaceRect();

	/// <summary>预览里背面的矩形（自检按它取样像素）。</summary>
	public Rect2 BackRectForTest
	{
		get
		{
			Rect2 face = FaceRect();
			return new Rect2(face.Position.X + face.Size.X + 16f, face.Position.Y, face.Size.X, face.Size.Y);
		}
	}

	/// <summary>预览矩形在屏幕上的位置（自检要把"控件坐标"换算成"截图坐标"）。</summary>
	public Rect2 FaceRectOnScreen() => ToScreen(FaceRectForTest);

	public Rect2 BackRectOnScreen() => ToScreen(BackRectForTest);

	/// <summary>
	/// 控件局部坐标 → 屏幕坐标。
	///
	/// 截图是屏幕空间的，而 <c>_Draw</c> 用的是控件局部坐标；
	/// 少了这一步换算，取样点会整体偏掉面板的位置（左上角差 8,50 加上页签的偏移）——
	/// 而"取到的是一块空白"和"卡面没画出来"在像素统计里长得一模一样。
	/// </summary>
	private Rect2 ToScreen(Rect2 local)
	{
		Vector2 origin = GetGlobalRect().Position;
		return new Rect2(origin + local.Position, local.Size);
	}

	public void SetDefinition(CardDefinition? definition)
	{
		_definition = definition;
		QueueRedraw();
	}

	/// <summary>
	/// 实例级覆盖（键 → 值）。按实例编辑时预览要按"定义 + 覆盖"合成，
	/// 与桌上那张卡走同一个渲染函数 —— 否则会出现"预览是一个值、桌上另一个值"，
	/// 而那正是最让人不信任编辑器的症状（M5 的类注释里已经记过同一条理由）。
	/// </summary>
	private System.Collections.Generic.Dictionary<string, string>? _overrides;

	public void SetOverrides(System.Collections.Generic.Dictionary<string, string>? overrides)
	{
		bool changed = !SameOverrides(_overrides, overrides);

		// 存一份副本：调用方给的是那张卡的活字典，直接留着引用的话
		// "预览持有的是哪一份"会随时间漂移，而重画与否就再也判断不准了。
		_overrides = overrides == null
			? null
			: new System.Collections.Generic.Dictionary<string, string>(overrides);

		if (changed)
			QueueRedraw();
	}

	private static bool SameOverrides(
		System.Collections.Generic.Dictionary<string, string>? a,
		System.Collections.Generic.Dictionary<string, string>? b)
	{
		if (a is null || b is null)
			return a is null && b is null;

		if (a.Count != b.Count)
			return false;

		foreach (System.Collections.Generic.KeyValuePair<string, string> kv in a)
		{
			if (!b.TryGetValue(kv.Key, out string? v) || v != kv.Value)
				return false;
		}

		return true;
	}

	/// <summary>
	/// 卡片按固定宽高比排布，两侧各留一点边。
	///
	/// 宽高比取 <see cref="GameConfig.DefaultCardSize"/> 的比例而不是写死数字：
	/// 卡牌尺寸改了之后，预览必须跟着改 —— 否则"预览里字刚好放下、
	/// 桌上却溢出了"这种问题只能靠肉眼在两张图之间比。
	/// </summary>
	private Rect2 FaceRect()
	{
		Vector2 area = UsableArea();

		float gap = 16f;
		float slotWidth = Mathf.Max((area.X - (gap * 3f)) * 0.5f, 10f);

		float ratio = GameConfig.DefaultCardSize.Y / Mathf.Max(GameConfig.DefaultCardSize.X, 1f);
		float height = Mathf.Min(area.Y - (gap * 2f), slotWidth / ratio);
		float width = height * ratio;

		// 垂直居中、两张并排靠左
		float y = (area.Y - height) * 0.5f;
		return new Rect2(gap, y, width, height);
	}

	/// <summary>
	/// 真正拿来排版的那块面积。
	///
	/// <b>控件尺寸还没算出来时用一个默认面积，而不是"什么都不画"。</b>
	/// Godot 会跳过<b>不可见</b> Control 子树的布局，而编辑器默认是隐藏的
	/// （F1 才打开）—— 于是"打开面板的那一帧"里本控件的 <c>Size</c> 可能还是
	/// <c>(0,0)</c>，算式得到负宽高，<c>DrawTextureRect</c> 拿到负矩形不报错、只是不画。
	///
	/// 症状是<b>"预览框是空的"</b>，而面板、卡池列表、页签全都正常。
	/// M4 那条"0 高度面板能穿过全部判据"是同一个坑；这次自检的像素判据抓住了它
	/// （矩形 8×78、颜色数 0）。
	///
	/// 回退而不是提前 return：<b>预览必须永远画出点东西</b> ——
	/// 一个"偶尔空白"的预览会让人以为是自己没选中卡。
	/// </summary>
	private Vector2 UsableArea()
	{
		Vector2 size = Size;
		if (size.X >= 200f && size.Y >= 160f)
			return size;

		return new Vector2(
			Mathf.Max(size.X, FallbackArea.X),
			Mathf.Max(size.Y, FallbackArea.Y));
	}

	/// <summary>布局还没落定时的默认预览面积（够放下两张卡的比例）。</summary>
	private static readonly Vector2 FallbackArea = new(560f, 300f);

	public override void _Draw()
	{
		DrawCount++;

		Rect2 face = FaceRect();
		var back = new Rect2(face.Position.X + face.Size.X + 16f, face.Position.Y, face.Size.X, face.Size.Y);

		LastFaceRect = face;
		LastArea = UsableArea();

		// 底衬：没有定义时也得看得见一块区域，否则"预览是空的"和
		// "预览没画"在截图里长得一模一样。
		DrawRect(new Rect2(Vector2.Zero, Size), new Color(0f, 0f, 0f, 0.18f));

		if (_definition is null)
		{
			Vector2 at = new(12f, 24f);
			DrawString(Fonts.Ui, at, "（没有选中卡牌）", HorizontalAlignment.Left, -1f, 18,
				new Color("#d8dee9"));
			return;
		}

		// 与桌上那张卡<b>同一个函数</b>（见类注释）。
		CardFaceRenderer.DrawFace(this, _definition, face, _overrides);
		CardFaceRenderer.DrawBack(this, _definition, back);

		DrawString(Fonts.Ui, new Vector2(face.Position.X, face.Position.Y - 6f),
			"正面", HorizontalAlignment.Left, -1f, 16, new Color("#d8dee9"));
		DrawString(Fonts.Ui, new Vector2(back.Position.X, back.Position.Y - 6f),
			"背面", HorizontalAlignment.Left, -1f, 16, new Color("#d8dee9"));
	}
}

using Godot;

namespace TabletopSimulator.Data;

/// <summary>
/// 区域的类型。类型本身不带行为，只用来给 <see cref="ZoneDefaults"/> 挑一套出厂参数、
/// 以及决定右键菜单里哪些项该显示。
/// </summary>
public enum ZoneKind
{
	/// <summary>牌库：叠成一摞、默认盖放、双击抽牌。</summary>
	Deck,

	/// <summary>手牌：横排（或扇形）、默认翻开、有容量上限。</summary>
	Hand,

	/// <summary>弃牌堆：叠成一摞、翻开。</summary>
	Discard,

	/// <summary>出牌区：自由摆放，不排版、不改朝向。</summary>
	Play,

	/// <summary>公共区：自由摆放，任何物件都能进。</summary>
	Public,

	/// <summary>自定义。M5 的「画区域」默认给这个，全部参数手填。</summary>
	Custom,
}

/// <summary>
/// 区域怎么摆放它的成员。
///
/// 注意 <see cref="Free"/> 之外的三种都是<b>区域接管位置</b>：
/// 拖进去松手后，物件会被搬到区域算出来的位置，而不是停在鼠标松开的地方。
/// </summary>
public enum ZoneSortMode
{
	/// <summary>不排版 —— 拖进来放哪就是哪（出牌区 / 公共区）。</summary>
	Free,

	/// <summary>叠成一摞，位置阶梯错开，只画最上面几张（牌库 / 弃牌堆）。</summary>
	Stack,

	/// <summary>横排，超出宽度自动换行（手牌默认）。每张卡面都读得全。</summary>
	Row,

	/// <summary>横排 + 按中心对称扇形旋转（像真手牌，省地方但会互相叠压）。</summary>
	Fan,
}

/// <summary>
/// 物件进入区域后强制成什么朝向。
///
/// 为什么是三态而不是一个 <c>AutoFaceDown</c> 布尔：布尔表达不了「强制翻开」，
/// 而手牌区正需要它 —— 牌库抽出来的牌必须翻开，否则手里拿的全是卡背。
/// 一个布尔只能表达「进区盖放」，三态能同时表达牌库的<b>盖</b>和手牌的<b>翻</b>。
/// </summary>
public enum FaceOnEnter
{
	/// <summary>不动它（出牌区 / 公共区）。</summary>
	Unchanged,

	/// <summary>强制翻开（手牌 / 弃牌堆）。</summary>
	FaceUp,

	/// <summary>强制盖放（牌库）。</summary>
	FaceDown,
}

/// <summary>
/// 一块区域的完整定义 —— 纯 POCO，进 <c>project.json</c>。
///
/// 与 <see cref="CardDefinition"/> 同构：定义是可 JSON 序列化的数据，
/// 运行时节点（<c>Zone</c>）由它构造出来。存档要可读、可手改、可 diff。
/// </summary>
public sealed class ZoneDefinition
{
	/// <summary>存档内唯一 id。成员的 <c>ZoneId</c> 与其它区域的 <see cref="DrawTargetId"/> 靠它引用。</summary>
	public string Id { get; set; } = "";

	/// <summary>显示名，画在区域左上角，例如「牌库」。</summary>
	public string Name { get; set; } = "";

	public ZoneKind Kind { get; set; } = ZoneKind.Custom;

	/// <summary>区域矩形（世界坐标，左上角为原点）。</summary>
	public Rect2 Rect { get; set; }

	// ---- 排版 ----
	public ZoneSortMode SortMode { get; set; } = ZoneSortMode.Free;

	/// <summary>物件进区后的朝向处理。</summary>
	public FaceOnEnter FaceOnEnter { get; set; } = FaceOnEnter.Unchanged;

	/// <summary>拖入后是否强制归位到区域排出来的位置。false = 放哪算哪（但仍算属于这个区域）。</summary>
	public bool SnapOnDrop { get; set; } = true;

	// ---- 规则 ----
	/// <summary>容量上限。<b>0 = 无上限</b>。超限时整次拖入被拒绝（不是丢掉多出来的）。</summary>
	public int MaxCards { get; set; }

	/// <summary>双击是否抽牌。</summary>
	public bool DrawOnDoubleClick { get; set; }

	/// <summary>抽出来的牌进哪个区域。<b>空字符串 = 抽到桌面</b>；指向不存在的 id 时退回桌面。</summary>
	public string DrawTargetId { get; set; } = "";

	/// <summary>双击一次抽几张。</summary>
	public int DrawCount { get; set; } = 1;

	/// <summary>是否启用。关掉后不接受拖入、不响应双击与抽牌（「锁定」开关）。</summary>
	public bool Enabled { get; set; } = true;

	// ---- 外观 ----
	/// <summary>半透明底色。</summary>
	public Color Tint { get; set; } = ZoneDefaults.DefaultTint;

	public Color BorderColor { get; set; } = ZoneDefaults.DefaultBorder;

	/// <summary>
	/// 矩形中心 —— <see cref="ZoneSortMode.Stack"/> 的叠放锚点。
	///
	/// <b><c>[JsonIgnore]</c> 是必须的</b>：它是算出来的，不该进 <c>project.json</c>。
	/// <c>System.Text.Json</c> 默认会把它<b>写出去</b>、读的时候又静默丢弃 ——
	/// 于是存档里白多一行，还会与 <see cref="Rect"/> 漂移：
	/// 手改文件的人改了这一行，重启后毫无效果，因为真相在 <see cref="Rect"/> 里。
	/// </summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public Vector2 Center => Rect.Position + (Rect.Size * 0.5f);

	public ZoneDefinition Clone() => new()
	{
		Id = Id,
		Name = Name,
		Kind = Kind,
		Rect = Rect,
		SortMode = SortMode,
		FaceOnEnter = FaceOnEnter,
		SnapOnDrop = SnapOnDrop,
		MaxCards = MaxCards,
		DrawOnDoubleClick = DrawOnDoubleClick,
		DrawTargetId = DrawTargetId,
		DrawCount = DrawCount,
		Enabled = Enabled,
		Tint = Tint,
		BorderColor = BorderColor,
	};

	/// <summary>把朝向处理算成「进区后应该是盖着吗」。<see cref="FaceOnEnter.Unchanged"/> 时返回原值。</summary>
	public bool ResolveFaceDown(bool current) => FaceOnEnter switch
	{
		FaceOnEnter.FaceDown => true,
		FaceOnEnter.FaceUp => false,
		_ => current,
	};
}

/// <summary>
/// 每种区域类型的出厂参数。
///
/// 存在的意义：DemoContent 和 M5 的「画区域」都不该逐项填十几个参数 ——
/// 想要「一个牌库」就说 <c>ZoneDefaults.Create(ZoneKind.Deck, ...)</c>，
/// 得到的就是一个盖放的、可双击抽牌的、叠成摞的牌库。
/// </summary>
public static class ZoneDefaults
{
	/// <summary>叠放类区域（牌库 / 弃牌堆）的默认尺寸。</summary>
	public static readonly Vector2 StackZoneSize = new(300f, 460f);

	/// <summary>
	/// 手牌区尺寸。宽度是按<b>装满 10 张默认卡</b>反推的，不是随手填的：
	/// 10 × 300 + 9 × <see cref="GameConfig.ZoneRowGap"/> + 2 × <see cref="GameConfig.ZonePadding"/>
	/// = 3000 + 108 + 52 = 3160，留一点余量取 3168。
	///
	/// 早先这里写 1500（只够 4 张），结果第 5 张就换行掉到区域外 ——
	/// 手牌区是唯一会"长满"的区域，尺寸必须按上限算，不能按"差不多"估。
	/// </summary>
	public static readonly Vector2 HandZoneSize = new(3168f, 480f);
	public static readonly Vector2 PlayZoneSize = new(900f, 620f);
	public static readonly Vector2 PublicZoneSize = new(700f, 460f);
	public static readonly Vector2 CustomZoneSize = new(500f, 400f);

	public static readonly Color DefaultTint = new(1f, 1f, 1f, 0.06f);
	public static readonly Color DefaultBorder = new("#4c566a");

	// Nord 配色，与 M1/M2 一致：每种区域一个色相，桌上一眼能分辨
	private static readonly Color DeckColor = new("#5e81ac");     // 霜蓝
	private static readonly Color HandColor = new("#a3be8c");     // 苔绿
	private static readonly Color DiscardColor = new("#bf616a");  // 赤红
	private static readonly Color PlayColor = new("#ebcb8b");     // 亮黄
	private static readonly Color PublicColor = new("#b48ead");   // 紫
	private static readonly Color CustomColor = new("#4c566a");   // 灰

	/// <summary>区域底色：拿类型色压到很低的透明度，别盖住桌面网格。</summary>
	public static Color TintFor(ZoneKind kind)
	{
		Color c = ColorFor(kind);
		return new Color(c.R, c.G, c.B, 0.10f);
	}

	/// <summary>区域边框色。</summary>
	public static Color BorderFor(ZoneKind kind)
	{
		Color c = ColorFor(kind);
		return new Color(c.R, c.G, c.B, 0.55f);
	}

	private static Color ColorFor(ZoneKind kind) => kind switch
	{
		ZoneKind.Deck => DeckColor,
		ZoneKind.Hand => HandColor,
		ZoneKind.Discard => DiscardColor,
		ZoneKind.Play => PlayColor,
		ZoneKind.Public => PublicColor,
		_ => CustomColor,
	};

	/// <summary>默认尺寸。</summary>
	public static Vector2 SizeFor(ZoneKind kind) => kind switch
	{
		ZoneKind.Deck or ZoneKind.Discard => StackZoneSize,
		ZoneKind.Hand => HandZoneSize,
		ZoneKind.Play => PlayZoneSize,
		ZoneKind.Public => PublicZoneSize,
		_ => CustomZoneSize,
	};

	/// <summary>区域类型的中文名，画在矩形上、也用于菜单。</summary>
	public static string NameFor(ZoneKind kind) => kind switch
	{
		ZoneKind.Deck => "牌库",
		ZoneKind.Hand => "手牌",
		ZoneKind.Discard => "弃牌堆",
		ZoneKind.Play => "出牌区",
		ZoneKind.Public => "公共区",
		_ => "区域",
	};

	/// <summary>
	/// 按类型造一份定义，左上角放在 <paramref name="topLeft"/>。
	/// 这是造区域的<b>唯一推荐入口</b> —— 直接 new 一个 ZoneDefinition 会得到
	/// 全默认值（无上限、不排版、不抽牌），那是给 M5 手填用的。
	/// </summary>
	public static ZoneDefinition Create(ZoneKind kind, string id, string name, Vector2 topLeft)
	{
		ZoneDefinition def = new()
		{
			Id = id,
			Name = string.IsNullOrWhiteSpace(name) ? NameFor(kind) : name,
			Kind = kind,
			Rect = new Rect2(topLeft, SizeFor(kind)),
			Tint = TintFor(kind),
			BorderColor = BorderFor(kind),
		};

		switch (kind)
		{
			case ZoneKind.Deck:
				def.SortMode = ZoneSortMode.Stack;
				def.FaceOnEnter = FaceOnEnter.FaceDown;
				def.SnapOnDrop = true;
				def.DrawOnDoubleClick = true;
				def.DrawCount = 1;
				break;

			case ZoneKind.Hand:
				def.SortMode = ZoneSortMode.Row;   // 默认横排：验数值时卡面要读得全
				def.FaceOnEnter = FaceOnEnter.FaceUp;
				def.SnapOnDrop = true;
				def.MaxCards = 10;
				break;

			case ZoneKind.Discard:
				def.SortMode = ZoneSortMode.Stack;
				def.FaceOnEnter = FaceOnEnter.FaceUp;
				def.SnapOnDrop = true;
				break;

			case ZoneKind.Play:
			case ZoneKind.Public:
			case ZoneKind.Custom:
			default:
				def.SortMode = ZoneSortMode.Free;
				def.FaceOnEnter = FaceOnEnter.Unchanged;
				def.SnapOnDrop = false;
				break;
		}

		return def;
	}
}

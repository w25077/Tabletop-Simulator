using Godot;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core;

/// <summary>
/// 区域定义的增删改 + "在画布上拖矩形画一块区域"。
///
/// <b>为什么画区域要落成一个服务而不是写在页里：</b>
/// 拖矩形这条路的产物是"一块区域 + 一条撤销记录 + 一次计数广播"，
/// 而自检要能<b>直接</b>验"拖这么一下到底建出了什么"（矩形准不准、类型对不对、
/// 能不能 Ctrl+Z 撤掉）。写在页里就只能靠合成鼠标事件去点，那验的是
/// "鼠标事件有没有送到"，反而绕远了。
/// </summary>
public static class ZoneEditService
{
	/// <summary>上一次操作失败的原因。</summary>
	public static string LastError { get; private set; } = "";

	/// <summary>画出来的矩形至少要这么大才算数（像素，世界坐标）。</summary>
	public const float MinDrawSize = 80f;

	/// <summary>本次运行里被改过的区域 id（自检核对用）。</summary>
	private static readonly System.Collections.Generic.List<string> TouchedIds = new();

	public static System.Collections.Generic.IReadOnlyList<string> Touched => TouchedIds;

	// ------------------------------------------------------------------ 画区域

	/// <summary>
	/// 区域是否必须整个待在桌面内（M5.5 P3，用户拍板：**拒绝并说明**，不是静默夹回）。
	///
	/// 理由是"静默改用户的输入更糟"：用户填了 5000 宽的矩形、系统悄悄改成 3200，
	/// 他下次看到的是"我填的数没生效"。所以一次性输入（画 / 新建 / 表单）一律拒绝，
	/// 而<b>拖动改大小</b>走 <see cref="Mutate"/> 的夹取版 —— 每帧拒绝只会变成
	/// "拖到边上就不动了"外加一串错误提示。
	/// </summary>
	private static Board? _board;

	/// <summary>把桌面交给服务 —— 区域边界校验需要知道桌子多大。</summary>
	public static void BindBoard(Board board) => _board = board;

	/// <summary>
	/// 把"每块区域当前这个矩形是合法的"重新记一遍。
	///
	/// <b>读档 / 撤销之后必须调</b>：那两条路都会把区域矩形整个换掉，
	/// 而"上一次合法的矩形"这份备份会变成上一桌的陈旧值 ——
	/// 于是某次改动被拒时，区域会被写回一个<b>属于别的存档的坐标</b>。
	/// 这类"恢复成一份陈旧状态"的问题在本项目已经栽过好几次（见 M5 的
	/// "编辑器把内存里的旧场景写回去"），所以把它收成一个显式调用。
	/// </summary>
	public static void RememberAllRects(ZoneManager zones)
	{
		_lastGoodRect.Clear();

		foreach (Zone zone in zones.AllZones)
			_lastGoodRect[zone.Id] = zone.Definition.Rect;
	}

	/// <summary>区域超出桌面时的说明文字（拒绝时用；也便于自检核对文案）。</summary>
	public static string OutOfBoardMessage(Rect2 rect)
	{
		if (_board is null)
			return "";

		Rect2 board = _board.BoardRect;

		return $"超出桌面（桌面是 {board.Size.X:0}×{board.Size.Y:0}，"
			+ $"这块是 {rect.Size.X:0}×{rect.Size.Y:0}、左上角 {rect.Position.X:0},{rect.Position.Y:0}）";
	}

	/// <summary>区域是否在桌面内；<b>没有绑定桌面时一律放行</b>（服务不依赖场景也能跑）。</summary>
	public static bool FitsBoard(Rect2 rect) => _board is null || _board.Theme.Contains(rect);

	/// <summary>
	/// 画区域。
	///
	/// 三个细节都是有理由的：
	/// <list type="number">
	/// <item><b>从屏幕点换算而不是直接收世界坐标</b>：调用方（画布）手上只有屏幕点，
	///   在这里换算一次，省得每个调用点各自记"要除缩放、加相机偏移"。</item>
	/// <item><b>矩形规范化</b>：从右下往左上拖时 <c>Size</c> 会是负的，
	///   而 <c>Rect2</c> 的负尺寸会一路传到排版与命中测试里，
	///   症状是"区域画出来了，但拖牌进去不认"。</item>
	/// <item><b>太小的矩形当误触</b>：手抖一下不该在桌上留下一个 3×5 的区域 ——
	///   它几乎点不中，却会一直待在存档里。</item>
	/// </list>
	/// </summary>
	public static Zone? CreateFromDrag(
		ZoneManager zones, BoardCamera camera, Vector2 screenA, Vector2 screenB,
		ZoneKind kind, string name)
	{
		LastError = "";

		Rect2 rect = NormalizeRect(camera.ScreenToWorld(screenA), camera.ScreenToWorld(screenB));

		if (rect.Size.X < MinDrawSize || rect.Size.Y < MinDrawSize)
		{
			LastError = $"太小了（至少 {MinDrawSize:0}×{MinDrawSize:0}），已忽略";
			return null;
		}

		// 桌面边界（P3）：画出去的一角永远点不到，也没法用鼠标拖回来 —— 直接拒绝并说清楚。
		if (!FitsBoard(rect))
		{
			LastError = OutOfBoardMessage(rect) + " —— 已忽略";
			return null;
		}

		return Create(zones, kind, rect, name);
	}

	/// <summary>
	/// 按类型造一块区域（出厂参数交给 <see cref="ZoneDefaults"/>）。
	///
	/// <b>返回可空</b>：桌面边界校验（P3）会拒绝"伸出桌面"的矩形。
	/// 返回类型必须是 <c>Zone?</c> —— 第一版写的是返回 <c>Zone</c> 再 <c>return null!</c>，
	/// 那等于把一个必然的空引用藏在非空签名后面：调用方看不出要判空，
	/// 而它会在第一次 <c>zone.Definition</c> 时变成 <c>NullReferenceException</c>。
	/// 签名说真话，编译器才会站在我们这边。
	/// </summary>
	public static Zone? Create(ZoneManager zones, ZoneKind kind, Rect2 rect, string name)
	{
		LastError = "";

		// 桌面边界（P3）：新建这条一次性的路也是"拒绝并说明"。
		if (!FitsBoard(rect))
		{
			LastError = OutOfBoardMessage(rect);
			return null;
		}

		ZoneDefinition def = ZoneDefaults.Create(kind, NextId(zones), name, rect.Position);
		def.Rect = rect;   // ZoneDefaults 只按类型给尺寸，矩形宽高以画出来的为准

		Zone zone = zones.AddZone(def);
		Touch(zone.Id);
		RememberRect(zone);
		return zone;
	}

	/// <summary>规范化矩形：位置取两点的较小值，尺寸取绝对值。</summary>
	internal static Rect2 NormalizeRect(Vector2 a, Vector2 b)
	{
		float x = Mathf.Min(a.X, b.X);
		float y = Mathf.Min(a.Y, b.Y);
		return new Rect2(x, y, Mathf.Abs(a.X - b.X), Mathf.Abs(a.Y - b.Y));
	}

	// ------------------------------------------------------------------ 改区域

	/// <summary>
	/// 改一块区域的定义并<b>立刻生效</b>。
	///
	/// 走 <see cref="Zone.ApplyDefinition"/>：它内部会重算排版、重排成员、
	/// 并在成员次序变化时同步绘制次序（"谁是顶牌"那件事）。
	/// 只改 <c>Definition</c> 上的字段不会有任何变化 —— 症状是"改了没用"。
	/// </summary>
	public static bool Mutate(Zone zone, System.Action<ZoneDefinition> change, bool clampToBoard = false)
	{
		LastError = "";

		if (!GodotObject.IsInstanceValid(zone))
		{
			LastError = "这块区域已经不在场景里了";
			return false;
		}

		change(zone.Definition);

		// 桌面边界（P3）：
		// <list type="bullet">
		// <item><b>表单 / 新建这类一次性输入</b> → 拒绝并说明（默认）。</item>
		// <item><b>拖动改大小</b>（<paramref name="clampToBoard"/>）→ 夹回桌面内。
		//   拖动是<b>每帧一次</b>的连续输入，每帧拒绝只会变成"拖到边上就不动了"
		//   外加一串错误提示，用户根本不知道边界在哪。</item>
		// </list>
		if (clampToBoard && _board is not null)
		{
			Rect2 clamped = _board.Theme.Clamp(zone.Definition.Rect);

			if (!clamped.Position.IsEqualApprox(zone.Definition.Rect.Position)
				|| !clamped.Size.IsEqualApprox(zone.Definition.Rect.Size))
			{
				zone.Definition.Rect = clamped;
			}
		}
		else if (!FitsBoard(zone.Definition.Rect))
		{
			// 拒绝时必须把定义<b>写回去</b> —— change 是 Action、已经就地改过了。
			// 漏了这一步的话"拒绝"只体现在返回值上，而区域已经悄悄变形了。
			LastError = OutOfBoardMessage(zone.Definition.Rect);
			zone.Definition.Rect = _lastGoodRect.TryGetValue(zone.Id, out Rect2 good)
				? good
				: zone.Definition.Rect;
			zone.ApplyDefinition(zone.Definition);
			return false;
		}

		zone.ApplyDefinition(zone.Definition);
		Touch(zone.Id);
		RememberRect(zone);
		return true;
	}

	/// <summary>
	/// 每块区域"上一次通过校验的矩形"。
	///
	/// 为什么需要它：<see cref="Mutate"/> 收的是 <c>Action&lt;ZoneDefinition&gt;</c>，
	/// 调用方<b>就地改完</b>才轮到我们校验 —— 也就是说被拒时定义已经被改脏了。
	/// 没有这份备份，"拒绝"就只是一句返回值，区域已经变形。
	///
	/// 由 <see cref="RememberAllRects"/> 在开局与读档/撤销之后重建。
	/// </summary>
	private static readonly System.Collections.Generic.Dictionary<string, Rect2> _lastGoodRect = new();

	/// <summary>记下"这块区域当前这个矩形是合法的"。建区域与每次成功改动之后都要调。</summary>
	private static void RememberRect(Zone zone) => _lastGoodRect[zone.Id] = zone.Definition.Rect;

	/// <summary>桌上还有多少物件属于这块区域。</summary>
	public static int MemberCount(Zone zone) => GodotObject.IsInstanceValid(zone) ? zone.Count : 0;

	/// <summary>
	/// 删一块区域。
	///
	/// <b>区域里还有东西就拒绝</b>（与删卡牌定义同一条规矩）。
	/// 技术上删得掉（<c>RemoveZone</c> 会把成员清出去、卡片留在原地），
	/// 但那意味着"我删了一块空地"，回来却发现里面的 20 张牌全散在桌面上了 ——
	/// 一次误点要花十分钟收拾。所以先拒绝，并说清里面还有几张。
	/// </summary>
	public static bool Delete(ZoneManager zones, Zone zone)
	{
		LastError = "";

		if (!GodotObject.IsInstanceValid(zone))
		{
			LastError = "这块区域已经不在场景里了";
			return false;
		}

		if (zone.Count > 0)
		{
			LastError = $"「{zone.Definition.Name}」里还有 {zone.Count} 件东西，先把它们拿出来（或先挪走）再删";
			return false;
		}

		Touch(zone.Id);
		zones.RemoveZone(zone);
		return true;
	}

	/// <summary>区域 id 里出现过的最大序号之后的下一个（删了再加不会撞车）。</summary>
	private static string NextId(ZoneManager zones)
	{
		int max = 0;
		foreach (Zone zone in zones.AllZones)
		{
			string id = zone.Id;
			int dot = id.LastIndexOf('.');
			if (dot >= 0 && int.TryParse(id[(dot + 1)..], out int n) && n > max)
				max = n;
		}

		return $"zone.{max + 1}";
	}

	private static void Touch(string id)
	{
		if (!TouchedIds.Contains(id))
			TouchedIds.Add(id);
	}
}

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
	/// 用两个屏幕点圈出来的矩形建一块区域。
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

		return Create(zones, kind, rect, name);
	}

	/// <summary>按类型造一块区域（出厂参数交给 <see cref="ZoneDefaults"/>）。</summary>
	public static Zone Create(ZoneManager zones, ZoneKind kind, Rect2 rect, string name)
	{
		LastError = "";

		ZoneDefinition def = ZoneDefaults.Create(kind, NextId(zones), name, rect.Position);
		def.Rect = rect;   // ZoneDefaults 只按类型给尺寸，矩形宽高以画出来的为准

		Zone zone = zones.AddZone(def);
		Touch(zone.Id);
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
	public static bool Mutate(Zone zone, System.Action<ZoneDefinition> change)
	{
		LastError = "";

		if (!GodotObject.IsInstanceValid(zone))
		{
			LastError = "这块区域已经不在场景里了";
			return false;
		}

		change(zone.Definition);
		zone.ApplyDefinition(zone.Definition);
		Touch(zone.Id);
		return true;
	}

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

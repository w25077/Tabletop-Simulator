using System.Collections.Generic;
using Godot;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core;

/// <summary>
/// 区域与堆的**一致性不变量** —— 八条，跑一次给出红/绿。
///
/// <b>为什么要单独抽出来</b>：这八条原先写在 <c>DevReport</c> 里，只有自检在用。
/// M4 的撤销与读档做完之后，"把快照写回场景"本身就是最可能留下
/// 幽灵声明 / 残留徽章 / 半截成员关系的地方 —— 而那时的症状是
/// 「牌库显示 20 张、实际只抽得出 19 张」这种看起来只是数字不对的问题，
/// 靠肉眼与普通断言都极难定位。
///
/// 所以原来只给自检用的裁判，现在要让撤销系统也用它。
/// <b>共用一份实现</b>而不是各写一份：两份迟早会分叉，而分叉的那一刻
/// 自检就是一片绿色的假象。
///
/// 八条分别是：
/// <list type="number">
/// <item><c>zone_id_matches_membership</c> —— 成员必须自称属于它所在的那个区域</item>
/// <item><c>no_member_in_two_zones</c> —— 同一个物件不能同时出现在两个区域里</item>
/// <item><c>member_counts_match</c> —— 区域认为有 n 个成员，"自称属于它"的物件也得是 n 个</item>
/// <item><c>no_orphan_zone_claims</c> —— 不能有物件声称属于一个根本不存在的区域</item>
/// <item><c>stack_zones_contiguous</c> —— 叠放区域的成员序号恰好是 0..n-1</item>
/// <item><c>stack_visible_depth_ok</c> —— 只画最上面几张，下面那些必须不可见</item>
/// <item><c>pile_zone_exclusive</c> —— <c>PileId</c> 与 <c>ZoneId</c> 互斥</item>
/// <item><c>badge_only_when_stacked</c> —— 张数徽章只在真的处在叠放组里时出现</item>
/// </list>
/// </summary>
internal static class ZoneInvariants
{
	/// <summary>
	/// 跑一遍八条不变量。
	///
	/// 调用时机有两类：<b>自检的某个时间点</b>（开局 / 一整套操作之后），
	/// 以及<b>撤销或读档写回快照之后</b>。后者是这个抽象存在的理由。
	/// </summary>
	internal static ZoneInvariantReport Check(ObjectManager objects, ZoneManager zones)
	{
		var report = new ZoneInvariantReport();

		// 反向统计：每个区域 id 被多少物件"自称"属于
		var claimed = new Dictionary<string, int>();

		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (string.IsNullOrEmpty(obj.ZoneId))
				continue;

			claimed.TryGetValue(obj.ZoneId, out int n);
			claimed[obj.ZoneId] = n + 1;

			// PileId 与 ZoneId 必须互斥。同时非零意味着"M2 的自由堆生命周期"
			// 和"区域的排版"都会去改同一个物件的位置与可见性，必然打架。
			if (obj.PileId != 0)
				report.PileZoneOverlapCount++;
		}

		var memberUids = new HashSet<string>();

		foreach (Zone zone in zones.AllZones)
		{
			int n = zone.Count;
			report.TotalMembers += n;
			report.SnapshotZone(zone);

			for (int i = 0; i < n; i++)
			{
				TabletopObject m = zone.Members[i];

				// 成员可能已经被 QueueFree（撤销里删物件就走这条路）——
				// 这时它不是"数据不一致"，只是还没到 end of frame 被真正释放。
				if (!GodotObject.IsInstanceValid(m))
				{
					report.ZoneIdMatchesMembership = false;
					continue;
				}

				report.SeenMembers++;

				// 同一个物件不能同时出现在两个区域里
				if (!memberUids.Add(m.Uid))
					report.NoMemberInTwoZones = false;

				// 成员必须自称属于本区域
				if (m.ZoneId != zone.Id)
					report.ZoneIdMatchesMembership = false;

				if (zone.Definition.SortMode == ZoneSortMode.Stack)
				{
					// 叠放区域的成员序号必须恰好是 0..n-1（排版函数写的就是这个）
					if (m.PileIndex != i)
						report.StackZonesContiguous = false;

					// 只画最上面 PileVisibleDepth 张 —— 下面那些必须是不可见的
					bool shouldBeVisible = i >= n - GameConfig.PileVisibleDepth;
					if (m.Visible != shouldBeVisible)
						report.StackVisibleDepthOk = false;
				}
			}

			// 区域认为自己有 n 个成员，那么"自称属于它"的物件也必须是 n 个
			claimed.TryGetValue(zone.Id, out int claimedCount);
			if (claimedCount != n)
				report.MemberCountsMatch = false;
		}

		// 有区域 id 被物件自称属于，却根本没有这个区域 —— 只在换档 / 删区域时才出现
		foreach (KeyValuePair<string, int> kv in claimed)
		{
			if (zones.Find(kv.Key) is not null)
				continue;

			report.NoOrphanZoneClaims = false;

			// 把具体是哪些物件、指着哪个不存在的区域写下来。
			// 只给一个 false 的话，排查要回去把整张桌子翻一遍 ——
			// 而这条不变量恰恰是撤销/读档之后最常被打破的一条。
			foreach (TabletopObject obj in objects.AllObjects)
			{
				if (obj.ZoneId != kv.Key)
					continue;

				if (report.OrphanClaims.Count < 8)
					report.OrphanClaims.Add($"{obj.Uid} → 不存在的区域 '{kv.Key}' ({kv.Value} 个物件指着它)");

				break;
			}
		}

		// 张数徽章只允许在"物件真的处在一个叠放组里"时出现。
		//
		// 这一条是补的洞：上面那些不变量全都在看「区域内部」，
		// 没有任何一条管「物件离开叠放语境之后有没有留下残留」。
		// 于是出现了这样一个 bug —— 把一张牌放进牌库再拖出来，
		// 它右上角永远挂着牌库的张数（用户实测：拖出来还带着「30」）。
		// 数据上它已经不属于任何地方，只有 PileIndex/PileCount 两项是旧的，
		// 恰好满足徽章绘制条件（PileCount >= 2 且 PileIndex == PileCount - 1）。
		//
		// <b>判据按上下文推，不读 PileCount 当真相。</b> PileCount 是派生值
		// （= 它所在叠放组的成员数），ObjectState 里根本不存它 ——
		// 拿它跟"真相"比，等于让一个可能过期的缓存去当裁判。
		// M4 恢复快照时就因此报过假红。
		//
		// 口径刻意<b>不含"是不是顶张"</b>：产品现在给叠放区域的所有成员都写
		// 真实张数，靠 DrawPileBadge 的序号条件决定画不画 —— 开局桌面上
		// 20 张牌库牌带着 PileCount=20 是<b>既有的正常状态</b>，
		// 把它判红就成了"改产品去迁就断言"。
		// 这里只问一件事：这个字段与它所属叠放组的真实张数一致吗？
		// 不在任何叠放组里却还留着张数，才是 M3 那个"从牌库拖出来还挂着 30"的 bug。
		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (!GodotObject.IsInstanceValid(obj))
				continue;

			int groupCount = 0;

			if (obj.PileId != 0 && objects.Piles.TryGetValue(obj.PileId, out Pile? pile))
				groupCount = pile.Count;
			else if (!string.IsNullOrEmpty(obj.ZoneId) && zones.Find(obj.ZoneId) is Zone owner
				&& owner.Definition.SortMode == ZoneSortMode.Stack)
				groupCount = owner.Count;

			if (obj.PileCount == groupCount)
				continue;

			report.PhantomBadgeCount++;

			if (report.PhantomBadgeDetail.Count < 8)
			{
				report.PhantomBadgeDetail.Add(
					$"{obj.Uid} zone='{obj.ZoneId}' pile={obj.PileId} "
					+ $"index={obj.PileIndex} count={obj.PileCount}（应为 {groupCount}）");
			}
		}

		return report;
	}
}

/// <summary>
/// 一次不变量检查的结果。
///
/// <see cref="Invariants"/> 做成 <see cref="Godot.Collections.Dictionary"/> 是有意的：
/// 自检报告直接把这份字典嵌进 JSON，而"聚合判定遍历整个字典"正是
/// M2 学到的教训 —— 不逐个写键名，就从根上杜绝
/// "子项写一个名、聚合读另一个名"（那次五项子断言全绿却报失败）。
/// </summary>
internal sealed class ZoneInvariantReport
{
	// 八条不变量，默认全绿，由 Check 逐条否决
	internal bool ZoneIdMatchesMembership { get; set; } = true;
	internal bool NoMemberInTwoZones { get; set; } = true;
	internal bool MemberCountsMatch { get; set; } = true;
	internal bool NoOrphanZoneClaims { get; set; } = true;
	internal bool StackZonesContiguous { get; set; } = true;
	internal bool StackVisibleDepthOk { get; set; } = true;

	// 后两条是计数型的，判定在 All 里
	internal int PileZoneOverlapCount { get; set; }
	internal int PhantomBadgeCount { get; set; }

	internal List<string> PhantomBadgeDetail { get; } = new();

	/// <summary>孤儿声明的具体例子（uid → 不存在的区域 id）。与 <see cref="PhantomBadgeDetail"/> 同理：
	/// 只报 false 而不说是谁，排查成本全落在人身上。</summary>
	internal List<string> OrphanClaims { get; } = new();

	/// <summary>
	/// 逐区域的现场摘要（id / 排版 / 张数 / 各成员的序号与可见性）。
	///
	/// 存在的理由很具体：<c>stack_zones_contiguous</c> 与 <c>stack_visible_depth_ok</c>
	/// 只在<b>叠放</b>区域上生效，而它们变红时报告里只有一个 false ——
	/// 你无法分辨"序号错了"和"这个区域压根不是叠放模式了"。
	/// M4 恢复快照时就撞上过这个歧义，于是把现场摊进报告。
	/// </summary>
	internal List<string> ZoneSnapshots { get; } = new();

	internal void SnapshotZone(Zone zone)
	{
		if (ZoneSnapshots.Count >= 12)
			return;

		var sb = new System.Text.StringBuilder();
		sb.Append(zone.Id).Append(" [").Append(zone.Definition.SortMode)
			.Append("] 张数=").Append(zone.Count).Append(" 成员=[");

		for (int i = 0; i < zone.Count; i++)
		{
			TabletopObject m = zone.Members[i];

			if (i > 0)
				sb.Append(' ');

			if (!GodotObject.IsInstanceValid(m))
			{
				sb.Append("<失效>");
				continue;
			}

			sb.Append(m.Uid).Append('#').Append(m.PileIndex)
				.Append(m.Visible ? "可见" : "隐藏");
		}

		sb.Append(']');
		ZoneSnapshots.Add(sb.ToString());
	}

	// 统计（报告用）
	internal int TotalMembers { get; set; }
	internal int SeenMembers { get; set; }

	/// <summary>八条是否全绿。</summary>
	internal bool All =>
		ZoneIdMatchesMembership
		&& NoMemberInTwoZones
		&& MemberCountsMatch
		&& NoOrphanZoneClaims
		&& StackZonesContiguous
		&& StackVisibleDepthOk
		&& PileZoneOverlapCount == 0
		&& PhantomBadgeCount == 0;

	/// <summary>八条不变量做成字典，直接进自检报告。</summary>
	internal Godot.Collections.Dictionary Invariants => new()
	{
		["zone_id_matches_membership"] = ZoneIdMatchesMembership,
		["no_member_in_two_zones"] = NoMemberInTwoZones,
		["member_counts_match"] = MemberCountsMatch,
		["no_orphan_zone_claims"] = NoOrphanZoneClaims,
		["stack_zones_contiguous"] = StackZonesContiguous,
		["stack_visible_depth_ok"] = StackVisibleDepthOk,
		["pile_zone_exclusive"] = PileZoneOverlapCount == 0,
		["badge_only_when_stacked"] = PhantomBadgeCount == 0,
	};

	/// <summary>失败的那几条的键名，用于打日志（全绿时为空）。</summary>
	internal List<string> Failures()
	{
		var list = new List<string>();
		foreach (KeyValuePair<Variant, Variant> kv in Invariants)
		{
			if (!kv.Value.AsBool())
				list.Add(kv.Key.ToString());
		}

		return list;
	}
}

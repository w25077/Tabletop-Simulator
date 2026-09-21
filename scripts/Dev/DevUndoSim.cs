using System.Collections.Generic;
using Godot;
using TabletopSimulator.Core;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Dev;

/// <summary>
/// 「把快照写回场景」的自检。
///
/// 这是 M4 撤销系统的<b>地基断言</b>：如果快照写回做不到逐字段一致，
/// 那么建在它上面的 <c>CommandHistory</c> 无论怎么写都是错的 ——
/// 而它的失败模式（位置差 0.3px、少一个 PileCount、区域次序换了）
/// 全都不会报错，只会在你验玩法时悄悄给出**错误的结论**。
///
/// 主体是一条"往返一致"断言：记下开局快照 → 让整轮自检把桌子折腾一遍
/// （DevZoneSim 那一百多条断言干的就是这个）→ 写回快照 → 与开局逐字段比对。
/// 用现成的折腾过程而不是自己再造一遍，好处是它<b>真的复杂</b>：
/// 抽牌、打出、弃牌、洗牌、整摞翻转、成摞、拆堆、删物件、复制……
/// 快照要覆盖的每一种状态都出现过了。
/// </summary>
internal static class DevUndoSim
{
	private sealed class Checks
	{
		private readonly Godot.Collections.Dictionary _dict;
		private readonly List<string> _keys = new();

		internal Checks(Godot.Collections.Dictionary dict) => _dict = dict;

		internal void Put(string key, bool value)
		{
			_dict[key] = value;
			_keys.Add(key);
		}

		internal void Data(string key, Variant value) => _dict[key] = value;

		internal int Count => _keys.Count;

		internal int Passed()
		{
			int n = 0;
			foreach (string k in _keys)
			{
				if (_dict[k].AsBool())
					n++;
			}

			return n;
		}

		internal bool AllPass()
		{
			if (_keys.Count == 0)
				return false;

			foreach (string k in _keys)
			{
				if (!_dict[k].AsBool())
					return false;
			}

			return true;
		}
	}

	/// <summary>
	/// 在<b>开局时刻</b>记下快照。必须在任何操作探针之前调用 ——
	/// 这一点和 <c>DevReport.ZoneProbe</c> 那份"开局不变量"是同一个道理。
	/// </summary>
	internal static SceneSnapshot CaptureBaseline(ObjectManager objects, ZoneManager zones)
		=> SceneSnapshot.Capture(objects, zones);

	/// <summary>
	/// 把开局快照写回去，然后逐字段比对。
	/// 由 <c>DevCapture</c> 排在<b>所有其它探针之后</b>调用 ——
	/// 它前面那一百多条断言正好构成了"把桌子折腾乱"的过程。
	/// </summary>
	internal static Godot.Collections.Dictionary RestoreAndCompare(
		ObjectManager objects, ZoneManager zones, SceneSnapshot baseline)
	{
		var r = new Godot.Collections.Dictionary();
		var c = new Checks(r);

		// 先确认"桌子确实被折腾过"，否则下面的比对可能是"什么都没变"而蒙过去
		int objectsNow = objects.AllObjects.Count;
		int objectsBaseline = baseline.Count;
		int zonesNow = zones.ZoneCount;

		c.Put("setup_something_changed",
			objectsNow != objectsBaseline || zonesNow != baseline.ZoneMembers.Count);

		r["objects_before_restore"] = objectsNow;
		r["objects_in_snapshot"] = objectsBaseline;

		// ---- 写回 ----
		baseline.Restore(objects, zones);

		// ---- 逐字段比对 ----
		List<ObjectState> now = objects.CaptureAllStates();
		var mismatches = new Godot.Collections.Array();

		c.Put("object_count_matches", now.Count == baseline.Objects.Count);
		r["objects_after_restore"] = now.Count;

		// 区域成员的"位置/序号/可见性"由<b>区域排版</b>决定，不由物件自己决定
		// （ApplyStack 会按成员次序重写 PileIndex / PileCount / Visible，
		//  Row 排版会重算位置）。所以对这些字段要比"共享上的次序"，
		// 而不是比快照里那份可能已经陈旧的副本 —— 而那件事已经由
		// zone_members_match + zone_layout_is_authoritative 两条严格验过了。
		var inAnyZone = new HashSet<string>();
		foreach (Zone zone in zones.AllZones)
		{
			foreach (TabletopObject m in zone.Members)
			{
				if (GodotObject.IsInstanceValid(m))
					inAnyZone.Add(m.Uid);
			}
		}

		int compared = 0;
		int fieldDiffs = 0;

		for (int i = 0; i < baseline.Objects.Count && i < now.Count; i++)
		{
			ObjectState a = baseline.Objects[i];
			ObjectState b = now[i];
			compared++;

			if (a.Uid != b.Uid)
			{
				fieldDiffs++;
				Add(mismatches, $"#{i} uid: {a.Uid} != {b.Uid}");
				continue;   // uid 都对不上，后面逐字段比没有意义
			}

			bool zoneMember = inAnyZone.Contains(a.Uid);

			foreach (string diff in DiffFields(a, b, zoneMember))
			{
				fieldDiffs++;
				Add(mismatches, $"{a.Uid}{(zoneMember ? "（区域成员）" : "")} {diff}");
			}
		}

		c.Put("every_field_matches", fieldDiffs == 0);
		r["compared_objects"] = compared;
		r["field_diff_count"] = fieldDiffs;
		r["mismatches"] = mismatches;
		r["extras_removed"] = baseline.LastExtraRemoved;

		// ---- 区域成员次序（牌库的顶牌是谁，本身就是数据）----
		int zoneDiff = 0;
		var zoneDiffs = new Godot.Collections.Array();

		foreach (Zone zone in zones.AllZones)
		{
			var actual = new List<string>();
			foreach (TabletopObject m in zone.Members)
			{
				if (GodotObject.IsInstanceValid(m))
					actual.Add(m.Uid);
			}

			if (!baseline.ZoneMembers.TryGetValue(zone.Id, out List<string>? want))
				want = new List<string>();

			if (actual.Count != want.Count)
			{
				zoneDiff++;
				Add(zoneDiffs, $"{zone.Id} 张数 {actual.Count} != {want.Count}");
				continue;
			}

			for (int i = 0; i < want.Count; i++)
			{
				if (actual[i] != want[i])
				{
					zoneDiff++;
					Add(zoneDiffs, $"{zone.Id} 第 {i} 张 {actual[i]} != {want[i]}");
					break;
				}
			}
		}

		c.Put("zone_members_match", zoneDiff == 0);
		r["zone_diff_count"] = zoneDiff;
		r["zone_diffs"] = zoneDiffs;

		// ---- 绘制次序（谁压谁）----
		int orderDiff = 0;
		for (int i = 0; i < baseline.Objects.Count && i < now.Count; i++)
		{
			if (baseline.Objects[i].Uid != now[i].Uid)
			{
				orderDiff++;
				break;
			}
		}

		c.Put("draw_order_matches", orderDiff == 0);

		// ---- uid 唯一性 ----
		//
		// 这条是 M4 做快照写回时**顺手抓到的真 bug**：DuplicateObjects 复制了原件的 uid，
		// 于是 60 个物件只映射出 36 个不同 uid —— 桌面上有一半是"重名"的。
		// 危险在于按 uid 找物件的地方（堆成员、区域成员、存档、快照写回）
		// 会随机命中一个，而两个物件在代码里完全一样。
		//
		// 断言放在这里而不是只放在复制那一步：**任何**操作之后桌面上都不该有重名。
		List<string> duplicateUids = FindDuplicateUids(objects);
		c.Put("uid_unique_on_board", duplicateUids.Count == 0);

		var dupArr = new Godot.Collections.Array();
		foreach (string uid in duplicateUids)
			dupArr.Add(uid);

		r["duplicate_uids"] = dupArr;

		// ---- uid 计数器 ----
		c.Put("uid_sequence_matches", objects.UidSequence == baseline.NextUidSeq);
		r["uid_sequence_after"] = objects.UidSequence;
		r["uid_sequence_snapshot"] = baseline.NextUidSeq;

		// ---- 写回之后一致性必须仍然全绿 ----
		//
		// 这条是 M4 最依赖的保证：撤销/读档之后八条区域不变量不能被破坏。
		// 它本身是否可信，由 invariant_probe 那一节证明（裁判会吹哨）。
		ZoneInvariantReport check = ZoneInvariants.Check(objects, zones);
		c.Put("invariants_hold_after_restore", check.All);

		var invariantFailures = new Godot.Collections.Array();
		foreach (string name in check.Failures())
			invariantFailures.Add(name);

		r["restore_invariant_failures"] = invariantFailures;

		var orphanDetail = new Godot.Collections.Array();
		foreach (string line in check.OrphanClaims)
			orphanDetail.Add(line);

		r["orphan_claims"] = orphanDetail;
		r["zones_after_restore"] = ToVariant(check.ZoneSnapshots);
		r["phantom_badges"] = ToVariant(check.PhantomBadgeDetail);
		r["restore_overlap_count"] = check.PileZoneOverlapCount;

		// 恢复后的自由堆清单（张数 + 成员）。留着是因为"撤销之后那一摞还在不在"
		// 是肉眼最该核对的一件事，而报告里只有逐物件的 pileId 不方便看。
		var pilesNow = new Godot.Collections.Array();
		foreach (KeyValuePair<int, Pile> kv in objects.Piles)
			pilesNow.Add($"#{kv.Key} 张数={kv.Value.Count}");

		r["piles_after_restore"] = pilesNow;

		// ---- 快照本身的自洽性（空快照不该被当成有效结果）----
		c.Put("baseline_was_not_empty", baseline.Objects.Count > 0);
		c.Put("baseline_has_zones", baseline.ZoneMembers.Count > 0);

		r["assertion_count"] = c.Count;
		r["passed"] = c.Passed();
		r["pass"] = c.AllPass();
		return r;
	}

	private static void Add(Godot.Collections.Array arr, string text)
	{
		if (arr.Count < 12)
			arr.Add(text);
	}

	private static Godot.Collections.Array ToVariant(List<string> items)
	{
		var arr = new Godot.Collections.Array();
		foreach (string s in items)
			arr.Add(s);

		return arr;
	}

	/// <summary>
	/// 桌面上出现重名的 uid（同一个 uid 被两个物件用着）。
	///
	/// 这类 bug 不会让任何东西崩，只会让**按 uid 找物件**的地方随机命中一个 ——
	/// 于是"撤销之后消失的不是那一张""读档后某张牌位置不对"。
	/// 因为两个物件在代码里长得完全一样，定位成本极高，所以单独做成一条断言。
	/// </summary>
	private static List<string> FindDuplicateUids(ObjectManager objects)
	{
		var seen = new HashSet<string>();
		var dup = new List<string>();

		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (!GodotObject.IsInstanceValid(obj))
				continue;

			if (!seen.Add(obj.Uid) && !dup.Contains(obj.Uid))
				dup.Add(obj.Uid);
		}

		return dup;
	}

	/// <summary>
	/// 逐字段比对一个物件的快照。
	///
	/// <b>位置用严格相等（<c>==</c>），不用 <c>IsEqualApprox</c>。</b>
	/// 这是刻意的：写回快照时的诱惑是"按锚点 + 阶梯偏移重算位置"，
	/// 那会引入亚像素误差 —— 而近似比较会把它放过去。严格相等逼着实现
	/// 老老实实把 <c>ApplyState</c> 写进去的位置当作权威。
	///
	/// <paramref name="zoneMember"/> 为真时跳过 <c>PileIndex</c>：
	/// 区域成员的叠放序号由区域排版按<b>它的</b>成员次序重写，
	/// 而快照里那份是上一轮的旧值。成员次序本身已由
	/// <c>zone_members_match</c> 严格验过，所以这里比它只会假红。
	/// （<c>PileCount</c> 不在 <c>ObjectState</c> 里，无需处理。）
	/// </summary>
	private static List<string> DiffFields(ObjectState a, ObjectState b, bool zoneMember)
	{
		var diffs = new List<string>();

		if (a.Kind != b.Kind)
			diffs.Add($"kind {a.Kind} != {b.Kind}");
		if (a.DefinitionId != b.DefinitionId)
			diffs.Add($"def {a.DefinitionId} != {b.DefinitionId}");
		if (a.Position != b.Position)
			diffs.Add($"pos {a.Position} != {b.Position}");
		if (a.RotationDegrees != b.RotationDegrees)
			diffs.Add($"rot {a.RotationDegrees} != {b.RotationDegrees}");
		if (a.FaceDown != b.FaceDown)
			diffs.Add($"faceDown {a.FaceDown} != {b.FaceDown}");
		if (a.PileId != b.PileId)
			diffs.Add($"pileId {a.PileId} != {b.PileId}");
		if (!zoneMember && a.PileIndex != b.PileIndex)
			diffs.Add($"pileIndex {a.PileIndex} != {b.PileIndex}");
		if (a.ZoneId != b.ZoneId)
			diffs.Add($"zoneId '{a.ZoneId}' != '{b.ZoneId}'");
		if (a.DiceSides != b.DiceSides || a.DiceCount != b.DiceCount)
			diffs.Add($"dice {a.DiceSides}x{a.DiceCount} != {b.DiceSides}x{b.DiceCount}");
		if (a.TokenTextOverride != b.TokenTextOverride)
			diffs.Add($"tokenText '{a.TokenTextOverride}' != '{b.TokenTextOverride}'");

		if (a.FieldOverrides.Count != b.FieldOverrides.Count)
		{
			diffs.Add($"overrides {a.FieldOverrides.Count} != {b.FieldOverrides.Count}");
		}
		else
		{
			foreach (KeyValuePair<string, string> kv in a.FieldOverrides)
			{
				if (!b.FieldOverrides.TryGetValue(kv.Key, out string? v) || v != kv.Value)
					diffs.Add($"override[{kv.Key}] != '{v}'");
			}
		}

		// 骰子点数也进快照（值列表）
		if (a.DiceValues.Count != b.DiceValues.Count)
		{
			diffs.Add($"diceValues {a.DiceValues.Count} != {b.DiceValues.Count}");
		}
		else
		{
			for (int i = 0; i < a.DiceValues.Count; i++)
			{
				if (a.DiceValues[i] != b.DiceValues[i])
				{
					diffs.Add($"diceValues[{i}] {a.DiceValues[i]} != {b.DiceValues[i]}");
					break;
				}
			}
		}

		return diffs;
	}
}

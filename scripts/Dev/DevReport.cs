using Godot;
using TabletopSimulator.Core;
using TabletopSimulator.Core.Objects;

namespace TabletopSimulator.Dev;

/// <summary>
/// 逐帧自检报告。把"这一帧到底对不对"变成可机械判读的数据，而不是靠人眼看图。
///
/// 为什么要这么做：截图本身只能给人看，而我需要能对每次改动做<b>可复现的判定</b>。
/// 报告覆盖四类证据：
/// <list type="number">
/// <item>字体：中文码位是否真的有字形（<c>Font.HasChar</c>）—— 直接证明界面不会出方块。</item>
/// <item>像素：分区域统计主色与颜色数 —— 证明桌面、网格、HUD 各自真的画出来了。</item>
/// <item>UI：关键 Control 的实际矩形与文本 —— 证明布局没塌、没跑出屏幕。</item>
/// <item>相机：缩放锚点不变量 —— 证明"以鼠标为中心缩放"的数学是对的。</item>
/// </list>
/// </summary>
internal static class DevReport
{
	/// <summary>用来验证中文字形覆盖的探针文本，取自界面上真实会出现的字。</summary>
	private const string CjkProbe = "桌面物件卡牌骰子区域存档撤销缩放适配平移网格导入图片自定义编辑器";

	internal static Godot.Collections.Dictionary Build(Node root, Image frame)
	{
		var report = new Godot.Collections.Dictionary();

		Viewport vp = root.GetViewport();
		Vector2I vpSize = (Vector2I)vp.GetVisibleRect().Size;

		report["viewport"] = new Godot.Collections.Array { vpSize.X, vpSize.Y };
		report["window_size"] = new Godot.Collections.Array {
			DisplayServer.WindowGetSize().X, DisplayServer.WindowGetSize().Y,
		};

		report["font"] = FontProbe();

		Node? main = root.GetNodeOrNull("Main");
		report["main_found"] = main is not null;
		if (main is null)
			return report;

		report["main_script"] = ScriptPath(main);
		report["main_children"] = ChildSummary(main);

		var nodes = new Godot.Collections.Dictionary
		{
			["Board"] = NodeProbe(main, "Board"),
			["HUD"] = NodeProbe(main, "HUD"),
			["Camera2D"] = NodeProbe(main, "Camera2D"),
			["Objects"] = NodeProbe(main, "Objects"),
			["Zones"] = NodeProbe(main, "Zones"),
		};
		report["nodes"] = nodes;

		report["hud_controls"] = ControlProbe(main, new[]
		{
			"HUD/HudRoot/Layout/TopBar",
			"HUD/HudRoot/Layout/TopBar/TopRow/SaveNameLabel",
			"HUD/HudRoot/Layout/TopBar/TopRow/ZoomLabel",
			"HUD/HudRoot/Layout/TopBar/TopRow/ObjectLabel",
			"HUD/HudRoot/Layout/TopBar/TopRow/SelectionLabel",
			"HUD/HudRoot/Layout/TopBar/TopRow/GridSnapButton",
			"HUD/HudRoot/Layout/TopBar/TopRow/FitButton",
			"HUD/HudRoot/Layout/TopBar/TopRow/Zoom100Button",
			"HUD/HudRoot/Layout/HintBar/HintLabel",
			"HUD/HudRoot/Layout/ToastLabel",
		});

		report["regions"] = RegionProbe(frame, vpSize);
		report["objects"] = ObjectProbe(main);
		report["zones"] = ZoneProbe(main);
		report["badge_render"] = BadgeProbe(main, frame);
		report["visual"] = VisualProbe(main, frame);

		if (main.GetNodeOrNull("Camera2D") is BoardCamera cam)
			report["camera_anchor_invariant"] = CameraAnchorProbe(cam);

		return report;
	}

	// ------------------------------------------------------------------ 像素级视觉断言

	/// <summary>
	/// 对每个物件做「渲染在你该在的位置、且是你该有的颜色」的机械断言。
	///
	/// 这是没有视觉能力时能做到的最强验证：把物件的世界包围盒投影到屏幕，
	/// 在它内部采样主色，再和它定义的填充色比对。
	/// 能一次性抓住「整个物件没画出来」「画错位置」「颜色串了」这三类问题。
	/// </summary>
	private static Godot.Collections.Dictionary VisualProbe(Node main, Image frame)
	{
		var result = new Godot.Collections.Dictionary();

		ObjectManager? objects = main.GetNodeOrNull<ObjectManager>("Objects");
		if (objects is null || main.GetNodeOrNull("Camera2D") is not BoardCamera cam)
		{
			result["skipped"] = "缺少物件管理器或相机";
			return result;
		}

		var items = new Godot.Collections.Array();
		int checkedCount = 0;
		int passCount = 0;
		var seenKinds = new System.Collections.Generic.Dictionary<Data.ObjectKind, int>();

		foreach (TabletopObject obj in objects.AllObjects)
		{
			// 上限放宽到能覆盖全部三种物件类型（卡 / Token / 骰子），
			// 但每种只取前几个 —— 报告别膨胀成整桌快照。
			if (checkedCount >= 20)
				break;

			if (!obj.Visible || !DevInputSim.IsOnScreen(cam, obj.Position))
				continue;

			if (seenKinds.TryGetValue(obj.Kind, out int seen) && seen >= 4)
				continue;

			Color expected = ExpectedFill(obj);
			Rect2 screenRect = ScreenRectOf(obj, cam);

			// 往里收一圈，避开描边与投影；剩下的区域主色应当是填充色
			float inset = Mathf.Min(screenRect.Size.X, screenRect.Size.Y) * 0.16f;
			Rect2 inner = screenRect.Grow(-inset);

			if (inner.Size.X < 4f || inner.Size.Y < 4f)
				continue;

			Color sampled = DominantColor(frame, inner);
			float distance = ColorDistance(expected, sampled);

			// 光看主色是不够的：就算文字/图案一个像素都没画出来，
			// 主色依然是填充色，断言照样通过。所以要单独统计"非填充色像素占比"，
			// 用它来证明卡面上确实有东西被画上去了。
			(float nonFillRatio, int distinctColors) = NonFillStats(frame, inner, expected);

			bool ok = distance < 0.08f;

			items.Add(new Godot.Collections.Dictionary
			{
				["uid"] = obj.Uid,
				["kind"] = obj.Kind.ToString(),
				["screen_rect"] = new Godot.Collections.Array
				{
					Mathf.Round(screenRect.Position.X), Mathf.Round(screenRect.Position.Y),
					Mathf.Round(screenRect.Size.X), Mathf.Round(screenRect.Size.Y),
				},
				["expected_hex"] = expected.ToHtml(true),
				["sampled_hex"] = sampled.ToHtml(true),
				["distance"] = distance,
				["non_fill_ratio"] = nonFillRatio,
				["distinct_colors"] = distinctColors,
				["content_ok"] = nonFillRatio > 0.01f,
				["ok"] = ok,
			});

			checkedCount++;
			seenKinds[obj.Kind] = (seenKinds.TryGetValue(obj.Kind, out int c) ? c : 0) + 1;

			if (ok)
				passCount++;
		}

		result["checked"] = checkedCount;
		result["passed"] = passCount;
		result["pass"] = checkedCount > 0 && passCount == checkedCount;
		result["items"] = items;
		return result;
	}

	/// <summary>物件的预期主色：正面卡 = 卡面底色，盖放 = 卡背底色，Token = 填充色，骰子 = 外壳色。</summary>
	private static Color ExpectedFill(TabletopObject obj) => obj switch
	{
		CardObject card => card.IsFaceDown ? card.Definition.BackTint : card.Definition.FaceTint,
		TokenObject token => token.Definition.Fill,
		DiceObject dice => dice.Tint,
		_ => Colors.Magenta,
	};

	/// <summary>把旋转后的物件投影到屏幕，取轴对齐包围盒。</summary>
	private static Rect2 ScreenRectOf(TabletopObject obj, BoardCamera cam)
	{
		Vector2 half = obj.Size * 0.5f;
		Vector2 p0 = obj.ToGlobal(new Vector2(-half.X, -half.Y));
		Vector2 p1 = obj.ToGlobal(new Vector2(half.X, -half.Y));
		Vector2 p2 = obj.ToGlobal(new Vector2(half.X, half.Y));
		Vector2 p3 = obj.ToGlobal(new Vector2(-half.X, half.Y));

		Vector2 s0 = cam.WorldToScreen(p0);
		Vector2 s1 = cam.WorldToScreen(p1);
		Vector2 s2 = cam.WorldToScreen(p2);
		Vector2 s3 = cam.WorldToScreen(p3);

		Vector2 min = s0.Min(s1).Min(s2).Min(s3);
		Vector2 max = s0.Max(s1).Max(s2).Max(s3);
		return new Rect2(min, max - min);
	}

	private static Color DominantColor(Image img, Rect2 region)
	{
		int x0 = Mathf.Max(Mathf.RoundToInt(region.Position.X), 0);
		int y0 = Mathf.Max(Mathf.RoundToInt(region.Position.Y), 0);
		int x1 = Mathf.Min(Mathf.RoundToInt(region.End.X), img.GetWidth());
		int y1 = Mathf.Min(Mathf.RoundToInt(region.End.Y), img.GetHeight());

		var counts = new System.Collections.Generic.Dictionary<uint, int>();
		uint best = 0;
		int bestCount = 0;

		for (int y = y0; y < y1; y += 2)
		{
			for (int x = x0; x < x1; x += 2)
			{
				uint c = img.GetPixel(x, y).ToRgba32();
				counts.TryGetValue(c, out int n);
				counts[c] = n + 1;

				if (counts[c] > bestCount)
				{
					bestCount = counts[c];
					best = c;
				}
			}
		}

		return Color.Color8(
			(byte)((best >> 24) & 0xFF),
			(byte)((best >> 16) & 0xFF),
			(byte)((best >> 8) & 0xFF),
			(byte)(best & 0xFF));
	}

	/// <summary>
	/// 统计区域内"与填充色明显不同"的像素占比，以及出现过的不同颜色数。
	/// 用途：证明卡面上确实画了东西（文字 / 卡背纹样 / 骰子数字），
	/// 因为主色断言对"什么都没画"和"画得很好"是区分不出来的。
	/// </summary>
	private static (float Ratio, int Distinct) NonFillStats(Image img, Rect2 region, Color fill)
	{
		int x0 = Mathf.Max(Mathf.RoundToInt(region.Position.X), 0);
		int y0 = Mathf.Max(Mathf.RoundToInt(region.Position.Y), 0);
		int x1 = Mathf.Min(Mathf.RoundToInt(region.End.X), img.GetWidth());
		int y1 = Mathf.Min(Mathf.RoundToInt(region.End.Y), img.GetHeight());

		var seen = new System.Collections.Generic.HashSet<uint>();
		int total = 0;
		int nonFill = 0;

		for (int y = y0; y < y1; y++)
		{
			for (int x = x0; x < x1; x++)
			{
				Color c = img.GetPixel(x, y);
				seen.Add(c.ToRgba32());
				total++;

				if (ColorDistance(c, fill) > 0.04f)
					nonFill++;
			}
		}

		return (total > 0 ? (float)nonFill / total : 0f, seen.Count);
	}

	/// <summary>归一化 RGB 距离（0 = 完全相同，1 = 最远）。</summary>
	private static float ColorDistance(Color a, Color b)
	{
		float dr = a.R - b.R;
		float dg = a.G - b.G;
		float db = a.B - b.B;
		return Mathf.Sqrt(((dr * dr) + (dg * dg) + (db * db)) / 3f);
	}

	// ------------------------------------------------------------------ 物件

	/// <summary>
	/// 桌面物件的结构快照：类型分布、堆叠情况、以及每个物件的关键状态。
	/// 物件的<b>行为</b>由 DevObjectSim 用合成输入验证；这里只回答"桌上有什么、在哪、什么状态"。
	/// </summary>
	private static Godot.Collections.Dictionary ObjectProbe(Node main)
	{
		ObjectManager? objects = main.GetNodeOrNull<ObjectManager>("Objects");
		if (objects is null)
			return new Godot.Collections.Dictionary { ["found"] = false };

		int cards = 0;
		int tokens = 0;
		int dice = 0;
		int faceDown = 0;
		int inPiles = 0;

		var list = new Godot.Collections.Array();
		int emitted = 0;

		foreach (TabletopObject obj in objects.AllObjects)
		{
			switch (obj.Kind)
			{
				case Data.ObjectKind.Card: cards++; break;
				case Data.ObjectKind.Token: tokens++; break;
				case Data.ObjectKind.Dice: dice++; break;
			}

			if (obj.IsFaceDown)
				faceDown++;

			if (obj.PileId != 0)
				inPiles++;

			// 报告别无限膨胀：只列前 48 个
			if (emitted < 48)
			{
				var entry = new Godot.Collections.Dictionary
				{
					["uid"] = obj.Uid,
					["kind"] = obj.Kind.ToString(),
					["pos"] = new Godot.Collections.Array { obj.Position.X, obj.Position.Y },
					["rot"] = obj.RotationDeg,
					["face_down"] = obj.IsFaceDown,
					["pile_id"] = obj.PileId,
					["pile_index"] = obj.PileIndex,
					["pile_count"] = obj.PileCount,
					["visible"] = obj.Visible,
				};

				if (obj is DiceObject d)
				{
					entry["dice_sides"] = d.Sides;
					entry["dice_count"] = d.Count;
					entry["dice_values"] = ToArray(d.Values);
					entry["dice_rolling"] = d.IsRolling;
				}

				list.Add(entry);
				emitted++;
			}
		}

		var pileInfo = new Godot.Collections.Array();
		foreach (System.Collections.Generic.KeyValuePair<int, Pile> kv in objects.Piles)
		{
			pileInfo.Add(new Godot.Collections.Dictionary
			{
				["id"] = kv.Key,
				["count"] = kv.Value.Count,
				["anchor"] = new Godot.Collections.Array { kv.Value.Anchor.X, kv.Value.Anchor.Y },
			});
		}

		return new Godot.Collections.Dictionary
		{
			["found"] = true,
			["count"] = objects.ObjectCount,
			["cards"] = cards,
			["tokens"] = tokens,
			["dice"] = dice,
			["face_down"] = faceDown,
			["in_piles"] = inPiles,
			["piles"] = pileInfo,
			["selection_count"] = objects.Selection.Count,
			["grid_snap"] = objects.GridSnapEnabled,
			["card_definitions"] = objects.CardDefinitions.Count,
			["token_definitions"] = objects.TokenDefinitions.Count,
			["items"] = list,
		};
	}

	private static Godot.Collections.Array ToArray(System.Collections.Generic.IReadOnlyList<int> values)
	{
		var arr = new Godot.Collections.Array();
		foreach (int v in values)
			arr.Add(v);
		return arr;
	}

	// ------------------------------------------------------------------ 区域（M3）

	/// <summary>
	/// 区域结构快照 + <b>六条不变量</b>。
	///
	/// 为什么这里不只是"列出有哪些区域"：M3 引入了一个双簿记风险 ——
	/// 「物件自称属于某区域」（<c>object.ZoneId</c>）与「区域认为自己有哪些成员」
	/// （<c>Zone.Members</c>）是两份数据。它们一旦不一致，症状是
	/// "牌库显示 20 张、实际只能抽出 19 张"这种**看起来只是数字不对**的问题，
	/// 靠人眼和普通断言都极难定位。
	///
	/// 所以把这层一致性直接翻译成几条不等式，让它在报告里变成一条红/绿。
	/// </summary>
	internal static Godot.Collections.Dictionary ZoneProbe(Node main)
	{
		var result = new Godot.Collections.Dictionary();

		ObjectManager? objects = main.GetNodeOrNull<ObjectManager>("Objects");
		ZoneManager? zones = main.GetNodeOrNull<ZoneManager>("Zones");

		if (objects is null || zones is null)
		{
			// 提前返回也<b>必须</b>带上 pass —— 否则调用方读 after["pass"] 会抛
			// KeyNotFoundException，而真正的症状（"区域探针根本没找到节点"）
			// 会被这个二次异常盖掉，排查方向直接跑偏。这一轮就栽在这上面。
			result["found"] = false;
			result["note"] = objects is null
				? "在传入的节点下找不到 ObjectManager（Objects）—— 传的是 Main 节点吗？"
				: "在传入的节点下找不到 ZoneManager（Zones）—— 传的是 Main 节点吗？";
			result["pass"] = false;
			return result;
		}

		// 反向统计：每个区域 id 被多少物件"自称"属于
		var claimed = new System.Collections.Generic.Dictionary<string, int>();
		int pileZoneOverlap = 0;

		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (string.IsNullOrEmpty(obj.ZoneId))
				continue;

			claimed.TryGetValue(obj.ZoneId, out int n);
			claimed[obj.ZoneId] = n + 1;

			// PileId 与 ZoneId 必须互斥。同时非零意味着"M2 的自由堆生命周期"
			// 和"区域的排版"都会去改同一个物件的位置与可见性，必然打架。
			if (obj.PileId != 0)
				pileZoneOverlap++;
		}

		var items = new Godot.Collections.Array();
		bool idMatches = true;
		bool noDuplicates = true;
		bool countsMatch = true;
		bool stackContiguous = true;
		bool stackVisibleOk = true;
		int totalMembers = 0;
		int seenMembers = 0;
		var memberUids = new System.Collections.Generic.HashSet<string>();

		foreach (Zone zone in zones.AllZones)
		{
			int n = zone.Count;
			totalMembers += n;

			int faceDown = 0;
			int visible = 0;

			for (int i = 0; i < n; i++)
			{
				TabletopObject m = zone.Members[i];

				if (!GodotObject.IsInstanceValid(m))
				{
					idMatches = false;
					continue;
				}

				seenMembers++;

				// 同一个物件不能同时出现在两个区域里
				if (!memberUids.Add(m.Uid))
					noDuplicates = false;

				// 成员必须自称属于本区域
				if (m.ZoneId != zone.Id)
					idMatches = false;

				if (m.IsFaceDown)
					faceDown++;

				if (m.Visible)
					visible++;

				if (zone.Definition.SortMode == Data.ZoneSortMode.Stack)
				{
					// 叠放区域的成员序号必须恰好是 0..n-1（排版函数写的就是这个）
					if (m.PileIndex != i)
						stackContiguous = false;

					// 只画最上面 PileVisibleDepth 张 —— 下面那些必须是不可见的
					bool shouldBeVisible = i >= n - GameConfig.PileVisibleDepth;
					if (m.Visible != shouldBeVisible)
						stackVisibleOk = false;
				}
			}

			// 区域认为自己有 n 个成员，那么"自称属于它"的物件也必须是 n 个
			claimed.TryGetValue(zone.Id, out int claimedCount);
			if (claimedCount != n)
				countsMatch = false;

			items.Add(new Godot.Collections.Dictionary
			{
				["id"] = zone.Id,
				["name"] = zone.DisplayName,
				["kind"] = zone.Kind.ToString(),
				["sort_mode"] = zone.Definition.SortMode.ToString(),
				["face_on_enter"] = zone.Definition.FaceOnEnter.ToString(),
				["enabled"] = zone.Definition.Enabled,
				["max_cards"] = zone.Definition.MaxCards,
				["draw_on_double_click"] = zone.Definition.DrawOnDoubleClick,
				["draw_target"] = zone.Definition.DrawTargetId,
				["rect"] = new Godot.Collections.Array
				{
					zone.Definition.Rect.Position.X, zone.Definition.Rect.Position.Y,
					zone.Definition.Rect.Size.X, zone.Definition.Rect.Size.Y,
				},
				["member_count"] = n,
				["visible_count"] = visible,
				["face_down"] = faceDown,
				["last_shuffle_seed"] = zone.LastShuffleSeed,
				["top_uid"] = zone.Top?.Uid ?? "",
			});
		}

		// 有区域 id 被物件自称属于，却根本没有这个区域 —— 只在换档 / 删区域时才出现
		bool noOrphanClaims = true;
		foreach (System.Collections.Generic.KeyValuePair<string, int> kv in claimed)
		{
			if (zones.Find(kv.Key) is null)
				noOrphanClaims = false;
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
		// 所以判据写成：PileCount > 0 的物件，必须要么在某个自由堆里、
		// 要么在某个叠放区域的成员表里，且张数与真实张数一致。
		int phantomBadges = 0;
		var phantomDetail = new Godot.Collections.Array();

		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (obj.PileCount <= 0)
				continue;

			bool legit = false;

			if (obj.PileId != 0 && objects.Piles.TryGetValue(obj.PileId, out Pile? pile))
			{
				legit = obj.PileCount == pile.Count;
			}
			else if (!string.IsNullOrEmpty(obj.ZoneId) && zones.Find(obj.ZoneId) is Zone owner)
			{
				legit = owner.Definition.SortMode == Data.ZoneSortMode.Stack
					&& obj.PileCount == owner.Count;
			}

			if (legit)
				continue;

			phantomBadges++;

			if (phantomDetail.Count < 8)
			{
				phantomDetail.Add(
					$"{obj.Uid} zone='{obj.ZoneId}' pile={obj.PileId} index={obj.PileIndex} count={obj.PileCount}");
			}
		}

		// 不变量集中在一个字典里。聚合判定直接把整个字典与一遍 ——
		// 不逐个写键名，从根上杜绝"子项写一个名、聚合读另一个名"
		// （M2 为此栽过一次：五项子断言全绿却报失败）。
		var invariants = new Godot.Collections.Dictionary
		{
			["zone_id_matches_membership"] = idMatches,
			["no_member_in_two_zones"] = noDuplicates,
			["member_counts_match"] = countsMatch,
			["no_orphan_zone_claims"] = noOrphanClaims,
			["stack_zones_contiguous"] = stackContiguous,
			["stack_visible_depth_ok"] = stackVisibleOk,
			["pile_zone_exclusive"] = pileZoneOverlap == 0,
			["badge_only_when_stacked"] = phantomBadges == 0,
		};

		bool all = true;
		foreach (System.Collections.Generic.KeyValuePair<Variant, Variant> kv in invariants)
		{
			if (!kv.Value.AsBool())
				all = false;
		}

		result["found"] = true;
		result["count"] = zones.ZoneCount;
		result["total_members"] = totalMembers;
		result["seen_members"] = seenMembers;
		result["pile_zone_overlap_count"] = pileZoneOverlap;
		result["phantom_badge_count"] = phantomBadges;
		result["phantom_badge_detail"] = phantomDetail;
		result["summary"] = zones.CountSummary();
		result["items"] = items;
		result["invariants"] = invariants;

		// 说清楚这份不变量的"时间点"，免得被误读成一条持续保证。
		// 本函数在**所有操作探针运行之前**调用（截图之后、模拟之前），
		// 所以它只能保证"开局那一刻是一致的"。
		// 运行期的一致性由 DevZoneSim 跑完一整套操作之后再查一遍，
		// 结果在 zone_simulation.invariants_after —— 那里才抓得住
		// "进出区域留下了残留"这类只有操作过才会出现的问题。
		result["snapshot"] = "开局状态（所有操作探针运行之前）；运行期见 zone_simulation.invariants_after";

		result["pass"] = zones.ZoneCount > 0 && all;
		return result;
	}

	// ------------------------------------------------------------------ 张数徽章（像素级）

	/// <summary>
	/// 张数徽章到底有没有被画出来 —— <b>在像素上验，不是查字段</b>。
	///
	/// 为什么需要这一节：已有的物件探针全都在比对"卡面主色"，而徽章画不画
	/// 对主色毫无影响 —— 所以「牌明明成了一堆、右上角却没有数字」这类问题
	/// 能一路穿过所有断言。用户实测就报了这么一条。
	///
	/// 做法是成对取样，缺一半就说明不了问题：
	/// <list type="bullet">
	/// <item><b>实验组</b>：某个 ≥2 的堆里，最上面那张的右上角。</item>
	/// <item><b>对照组</b>：一张不在任何堆里的散件卡，同样取右上角。</item>
	/// </list>
	/// 前者应当有徽章填充色（深蓝 <c>#2e3440</c>）的成片像素，后者应当几乎没有。
	/// 只查实验组是不够的 —— 万一那个颜色本来就到处都有，断言会永远绿。
	/// </summary>
	private static Godot.Collections.Dictionary BadgeProbe(Node main, Image frame)
	{
		var result = new Godot.Collections.Dictionary();

		ObjectManager? objects = main.GetNodeOrNull<ObjectManager>("Objects");
		if (objects is null || main.GetNodeOrNull("Camera2D") is not BoardCamera cam)
		{
			result["skipped"] = "缺少物件管理器或相机";
			return result;
		}

		Pile? pile = null;
		foreach (System.Collections.Generic.KeyValuePair<int, Pile> kv in objects.Piles)
		{
			if (kv.Value.Count >= 2 && kv.Value.Top is not null)
			{
				pile = kv.Value;
				break;
			}
		}

		TabletopObject? control = null;
		foreach (TabletopObject obj in objects.AllObjects)
		{
			// 对照组必须是<b>正面朝上</b>的卡。
			// 这里有个坑：卡背底色 #2b3242 和徽章填充色 #2e3440 的距离只有 0.008，
			// 拿一张盖放的牌当对照，它整片卡背都会被判成"徽章像素"（实测 4963 个），
			// 这条断言就废了。正面卡的几种底色与徽章色距离都 > 0.07，才是干净的对照。
			if (obj is CardObject card && !card.IsFaceDown && card.PileId == 0
				&& card.Visible && DevInputSim.IsOnScreen(cam, card.Position))
			{
				control = card;
				break;
			}
		}

		if (pile?.Top is not TabletopObject top || control is null)
		{
			result["skipped"] = "找不到「成堆的牌」或「散件对照卡」";
			return result;
		}

		// 取样的两张卡都必须完整落在视口里，否则采样区会被裁掉 ——
		// 那时数出来的是"没画"，而不是"没拍到"，报红会把人引向错误方向。
		// 近景截图（--zoom 1）下桌面大半在视口外，正是这种情况，所以标记 skipped
		// （<b>刻意不写 pass</b>：没跑 ≠ 跑挂了，这条约定全项目一致）。
		if (!FullyVisible(cam, top, frame) || !FullyVisible(cam, control, frame))
		{
			result["skipped"] = "堆顶或对照卡不在视口内，徽章像素无从取样（近景视角下属正常）";
			result["top_card"] = top.Uid;
			result["control_card"] = control.Uid;
			return result;
		}

		int topHits = CountBadgePixels(frame, ScreenRectOf(top, cam));
		int controlHits = CountBadgePixels(frame, ScreenRectOf(control, cam));

		result["pile_id"] = pile.Id;
		result["pile_count"] = pile.Count;
		result["top_card"] = top.Uid;
		result["top_card_badge_pixels"] = topHits;
		result["control_card"] = control.Uid;
		result["control_card_badge_pixels"] = controlHits;

		// 堆顶应当有徽章、散件应当没有。
		// 阈值取 20：半径 30 的实心圆在 52% 缩放下约 15px 半径，
		// 就算只有下半部分落在采样区里也远远超过 20 个像素。
		bool topHas = topHits >= 20;
		bool controlHas = controlHits < 5;

		result["top_has_badge"] = topHas;
		result["control_has_no_badge"] = controlHas;
		result["pass"] = topHas && controlHas;
		return result;
	}

	/// <summary>物件的屏幕包围盒是否完整落在这一帧里（留一点边距，避开裁剪）。</summary>
	private static bool FullyVisible(BoardCamera cam, TabletopObject obj, Image frame)
	{
		Rect2 r = ScreenRectOf(obj, cam);

		return r.Position.X >= 4f
			&& r.Position.Y >= 4f
			&& r.End.X <= frame.GetWidth() - 4
			&& r.End.Y <= frame.GetHeight() - 4;
	}

	/// <summary>在卡面右上角那一块里数"徽章填充色"的像素。</summary>
	private static int CountBadgePixels(Image img, Rect2 cardScreenRect)
	{
		// 取右上角 40% × 40%：徽章圆心在本地 (End.X - r*0.3, Position.Y + r*0.3)，
		// 半径 30（世界单位），所以它必然落在这个角上。
		var region = new Rect2(
			cardScreenRect.Position.X + (cardScreenRect.Size.X * 0.60f),
			cardScreenRect.Position.Y,
			cardScreenRect.Size.X * 0.40f,
			cardScreenRect.Size.Y * 0.40f);

		int x0 = Mathf.Clamp(Mathf.RoundToInt(region.Position.X), 0, img.GetWidth());
		int y0 = Mathf.Clamp(Mathf.RoundToInt(region.Position.Y), 0, img.GetHeight());
		int x1 = Mathf.Clamp(Mathf.RoundToInt(region.End.X), 0, img.GetWidth());
		int y1 = Mathf.Clamp(Mathf.RoundToInt(region.End.Y), 0, img.GetHeight());

		Color badgeFill = GameConfig.PileBadgeFill;
		int hits = 0;

		for (int y = y0; y < y1; y++)
		{
			for (int x = x0; x < x1; x++)
			{
				if (ColorDistance(img.GetPixel(x, y), badgeFill) < 0.035f)
					hits++;
			}
		}

		return hits;
	}

	// ------------------------------------------------------------------ 字体

	private static Godot.Collections.Dictionary FontProbe()
	{
		Font font = Core.Fonts.Ui;

		var missing = new Godot.Collections.Array();
		foreach (char ch in CjkProbe)
		{
			if (char.IsWhiteSpace(ch))
				continue;

			if (!font.HasChar(ch))
				missing.Add(ch.ToString());
		}

		// 中文字符集在 Godot 里的脚本代号；Hans = 简体中文。
		bool hansSupported;
		bool hanSupported;
		try
		{
			hansSupported = font.IsScriptSupported("Hans");
			hanSupported = font.IsScriptSupported("Hani");
		}
		catch (System.Exception e)
		{
			GD.PushWarning($"[DevReport] IsScriptSupported 查询失败：{e.Message}");
			hansSupported = false;
			hanSupported = false;
		}

		return new Godot.Collections.Dictionary
		{
			["resource_path"] = Core.Fonts.FontResourcePath,
			["class"] = font.GetClass(),
			["font_name"] = font.GetFontName(),
			["is_system_font"] = font is SystemFont,
			["hans_script_supported"] = hansSupported,
			["han_script_supported"] = hanSupported,
			["probe_text"] = CjkProbe,
			["missing_chars"] = missing,
			["missing_count"] = missing.Count,
			["verdict"] = missing.Count == 0 ? "CJK_OK" : "CJK_MISSING_GLYPHS",
		};
	}

	// ------------------------------------------------------------------ 像素

	private static Godot.Collections.Dictionary RegionProbe(Image frame, Vector2I vp)
	{
		int w = vp.X;
		int h = vp.Y;

		return new Godot.Collections.Dictionary
		{
			// HUD 顶栏所在的那一条
			["top_bar"] = RegionStats(frame, 0, 0, w, Mathf.Min(44, h)),
			// HUD 底部提示条
			["bottom_bar"] = RegionStats(frame, 0, Mathf.Max(0, h - 44), w, h),
			// 桌面主体（避开顶栏和底栏）
			["board_interior"] = RegionStats(frame, Mathf.Max(0, w / 6), Mathf.Max(0, h / 4), w - (w / 6), h - (h / 4)),
		};
	}

	/// <summary>区域颜色统计：主色 + 不同颜色数。主色应等于桌面底色，颜色数应显著大于 1（说明网格/文字画出来了）。</summary>
	private static Godot.Collections.Dictionary RegionStats(Image img, int x0, int y0, int x1, int y1)
	{
		x0 = Mathf.Max(x0, 0);
		y0 = Mathf.Max(y0, 0);
		x1 = Mathf.Min(x1, img.GetWidth());
		y1 = Mathf.Min(y1, img.GetHeight());

		var counts = new System.Collections.Generic.Dictionary<uint, int>();
		int samples = 0;

		for (int y = y0; y < y1; y += 2)
		{
			for (int x = x0; x < x1; x += 2)
			{
				uint c = img.GetPixel(x, y).ToRgba32();
				counts.TryGetValue(c, out int n);
				counts[c] = n + 1;
				samples++;
			}
		}

		uint dominant = 0;
		int dominantCount = 0;
		foreach (System.Collections.Generic.KeyValuePair<uint, int> kv in counts)
		{
			if (kv.Value > dominantCount)
			{
				dominantCount = kv.Value;
				dominant = kv.Key;
			}
		}

		Color dominantColor = Color.Color8(
			(byte)((dominant >> 24) & 0xFF),
			(byte)((dominant >> 16) & 0xFF),
			(byte)((dominant >> 8) & 0xFF),
			(byte)(dominant & 0xFF));

		return new Godot.Collections.Dictionary
		{
			["rect"] = new Godot.Collections.Array { x0, y0, x1, y1 },
			["samples"] = samples,
			["distinct_colors"] = counts.Count,
			["dominant_hex"] = dominantColor.ToHtml(true),
			["dominant_share"] = samples > 0 ? (float)dominantCount / samples : 0f,
		};
	}

	// ------------------------------------------------------------------ 相机

	/// <summary>
	/// 验证"以某屏幕点为锚缩放"的核心不变量：缩放前后该点下的世界坐标不变。
	/// 这是滚轮缩放不跑偏的数学保证，也是对 M1 最关键的逻辑断言。
	/// </summary>
	private static Godot.Collections.Dictionary CameraAnchorProbe(BoardCamera cam)
	{
		Vector2 anchor = new(500f, 300f);
		Vector2 worldBefore = cam.ScreenToWorld(anchor);
		float zoomBefore = cam.ZoomLevel;
		Vector2 posBefore = cam.GlobalPosition;

		cam.ZoomAtScreenPoint(3f, anchor);
		Vector2 worldAfterZoomIn = cam.ScreenToWorld(anchor);
		float driftIn = worldBefore.DistanceTo(worldAfterZoomIn);

		cam.ZoomAtScreenPoint(-3f, anchor);
		Vector2 worldAfterRoundTrip = cam.ScreenToWorld(anchor);
		float driftRoundTrip = worldBefore.DistanceTo(worldAfterRoundTrip);

		cam.SetZoomLevel(zoomBefore, anchor);
		cam.CenterOn(posBefore);
		cam.SnapToTargets();

		return new Godot.Collections.Dictionary
		{
			["anchor_screen"] = new Godot.Collections.Array { anchor.X, anchor.Y },
			["world_before"] = new Godot.Collections.Array { worldBefore.X, worldBefore.Y },
			["zoom_before"] = zoomBefore,
			["drift_on_zoom_in_px"] = driftIn,
			["drift_round_trip_px"] = driftRoundTrip,
			["pass"] = driftIn < 0.01f && driftRoundTrip < 0.01f,
		};
	}

	// ------------------------------------------------------------------ 节点

	/// <summary>脚本资源路径。<c>Node.GetScript()</c> 返回 <see cref="Variant"/>，需要显式取成 Script。</summary>
	private static string ScriptPath(Node n)
	{
		Script? script = n.GetScript().As<Script>();
		return script?.ResourcePath ?? "(none)";
	}

	private static Godot.Collections.Dictionary NodeProbe(Node parent, string path)
	{
		Node? n = parent.GetNodeOrNull(path);
		if (n is null)
			return new Godot.Collections.Dictionary { ["found"] = false, ["path"] = path };

		var d = new Godot.Collections.Dictionary
		{
			["found"] = true,
			["class"] = n.GetClass(),
			["script"] = ScriptPath(n),
		};

		if (n is Node2D n2d)
		{
			d["position"] = new Godot.Collections.Array { n2d.GlobalPosition.X, n2d.GlobalPosition.Y };
			d["z_index"] = n2d.ZIndex;
		}

		if (n is Camera2D c)
		{
			d["zoom"] = new Godot.Collections.Array { c.Zoom.X, c.Zoom.Y };
			d["is_current"] = c.IsCurrent();
		}

		return d;
	}

	private static Godot.Collections.Dictionary ControlProbe(Node main, string[] paths)
	{
		var outDict = new Godot.Collections.Dictionary();

		foreach (string p in paths)
		{
			Control? c = main.GetNodeOrNull<Control>(p);
			if (c is null)
			{
				outDict[p] = new Godot.Collections.Dictionary { ["found"] = false };
				continue;
			}

			Rect2 r = c.GetGlobalRect();
			var entry = new Godot.Collections.Dictionary
			{
				["found"] = true,
				["visible"] = c.IsVisibleInTree(),
				["rect"] = new Godot.Collections.Array { r.Position.X, r.Position.Y, r.Size.X, r.Size.Y },
			};

			switch (c)
			{
				case Label l:
					entry["text"] = l.Text;
					break;
				case Button b:
					entry["text"] = b.Text;
					break;
			}

			outDict[p] = entry;
		}

		return outDict;
	}

	private static Godot.Collections.Array ChildSummary(Node n)
	{
		var arr = new Godot.Collections.Array();
		foreach (Node c in n.GetChildren())
			arr.Add($"{c.Name}:{c.GetClass()}");
		return arr;
	}
}

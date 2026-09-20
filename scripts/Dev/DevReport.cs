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

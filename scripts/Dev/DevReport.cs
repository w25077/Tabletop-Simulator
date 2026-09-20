using Godot;
using TabletopSimulator.Core;

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
			"HUD/HudRoot/Layout/TopBar/TopRow/FitButton",
			"HUD/HudRoot/Layout/TopBar/TopRow/Zoom100Button",
			"HUD/HudRoot/Layout/HintBar/HintLabel",
			"HUD/HudRoot/Layout/ToastLabel",
		});

		report["regions"] = RegionProbe(frame, vpSize);

		if (main.GetNodeOrNull("Camera2D") is BoardCamera cam)
			report["camera_anchor_invariant"] = CameraAnchorProbe(cam);

		return report;
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

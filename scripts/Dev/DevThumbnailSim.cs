using System.Collections.Generic;
using Godot;
using TabletopSimulator.Core;
using TabletopSimulator.Core.Objects;

namespace TabletopSimulator.Dev;

/// <summary>
/// 存档缩略图的断言（M5 遗留口子之一）。
///
/// <b>为什么值得单独一节：</b>缩略图这条路在 M4 就写了一半 ——
/// <c>SavePanel.CaptureThumbnail</c> 会写 <c>thumbs/board.png</c>、
/// <c>SaveSystem.ListSaves</c> 会扫它，但<b>从来没有任何一处调用或显示过</b>。
/// 于是"它到底写没写、写出来是什么、菜单里看不看得见"三件事全都没有裁判：
/// 一份全绿的报告完全可能对应一个从没抓过图的程序。
///
/// 这一节盯三件事：
/// <list type="number">
///   <item>保存确实产出了缩略图，尺寸是 <c>320×180</c>、并且落在这个存档目录里；</item>
///   <item>那张图<b>真的是这一桌</b> —— 按相机自己的换算投影取样，而不是"文件存在即通过"；</item>
///   <item><see cref="ThumbnailView"/> 会随文件内容重载（换一张图就换一张画），
///         菜单悬停能显示、挪开与关菜单能收掉，且框子不压在菜单上。</item>
/// </list>
///
/// <b>这一节写歪过三次，三个坑都留在注释里（都是"我猜错了"，不是产品坏了）：</b>
/// <list type="number">
///   <item>容差写 <c>0.06</c>，把 HUD 底色 <c>#242931</c> 认成了棋盘格 <c>#2e3440</c>
///     （两者距离只有 <c>0.0465</c>）—— 一条根本挡不住东西的断言；</item>
///   <item>取样点没先判"在不在桌上"，桌外那几个点拿背景色去比棋盘格；</item>
///   <item>用 <c>GetCanvasTransform()</c> 自己乘世界坐标 —— 那是<b>渲染值</b>，
///     而探针夹在两次处理之间，投影与已画进图里的位置差了一截。
///     改用相机自己的 <see cref="BoardCamera.WorldToScreen"/>（与 <c>DevInputSim</c> 同口径）
///     并先 <see cref="BoardCamera.SnapToTargets"/> 才对齐。</item>
/// </list>
/// </summary>
internal static class DevThumbnailSim
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

		/// <summary>
		/// 标一条"没条件跑"。按项目约定：跳过<b>不写进 <c>_keys</c></b>，
		/// 于是它既不谎报通过、也不误报失败（没有 <c>pass</c> 键 = 没跑）。
		/// </summary>
		internal void Skip(string reason) => _dict["skipped"] = reason;

		internal bool AllPass()
		{
			foreach (string k in _keys)
			{
				if (!_dict[k].AsBool())
					return false;
			}

			return true;
		}
	}

	/// <summary>棋盘格两种底色的容差。取 0.03 而不是 0.06 —— 见类注释里第一条教训。</summary>
	private const float PatternTolerance = 0.03f;

	internal static Godot.Collections.Dictionary Probe(
		Node host, Main main, ObjectManager objects, ThumbnailView thumb)
	{
		var r = new Godot.Collections.Dictionary();
		var c = new Checks(r);

		if (!AppPaths.UsingCustomRoot)
		{
			c.Skip("没有 --save-root，拒绝在真实存档目录上写缩略图");
			return r;
		}

		SavePanel saves = main.Save;

		// 绝对路径取，不用相对路径 —— 相对路径是从 <c>DevCapture</c> 节点起算的
		// （它挂在 Main 下），写 "HUD/..." 会找不到并当场抛 NodeNotFound。
		// 这一条是实测踩出来的：第一次跑就崩在这里。
		PopupMenu menu = host.GetNode<PopupMenu>("/root/Main/HUD/HudRoot/SaveMenu");

		// ---------------------------------------------------------- 1. 保存产出图
		Dictionary<string, ulong> before = SnapshotThumbs();

		SnapCamera(main);
		bool saved = saves.SaveNow();
		c.Put("save_now_succeeded", saved);
		r["save_error"] = SaveSystem.LastError;

		Dictionary<string, ulong> after = SnapshotThumbs();

		// 按"变化"找，而不是按"猜出来的路径"找：路径猜对了但文件没写出来，
		// 与路径本身就猜错，是两种不同的故障，混在一条断言里分不出来。
		var created = new List<string>();
		foreach (KeyValuePair<string, ulong> kv in after)
		{
			if (!before.TryGetValue(kv.Key, out ulong oldMtime) || oldMtime != kv.Value)
				created.Add(kv.Key);
		}

		r["thumbs_written"] = created.Count;
		var createdArray = new Godot.Collections.Array();
		foreach (string p in created)
			createdArray.Add(p);
		r["thumbs_written_paths"] = createdArray;

		c.Put("save_wrote_a_thumbnail", created.Count > 0);

		string path = created.Count > 0 ? created[0] : "";
		string expected = $"{AppPaths.ThumbsDir(saves.CurrentSave)}/board.png";
		c.Put("thumbnail_landed_in_the_current_save", path == expected);
		r["current_save"] = saves.CurrentSave;
		r["thumbnail_path"] = path;

		thumb.Show(path);
		c.Put("thumbnail_file_loads", thumb.HasTexture);

		// ---------------------------------------------------------- 2. 它真的是这一桌
		//
		// <b>量两次：面板关着一次、面板开着一次。</b>
		//
		// 这一段排在一长串探针之后，而 <c>editor_simulation</c> 收尾时面板还开着 ——
		// 所以"面板挡着缩略图"是一个真实存在、必须知道结论的局面。
		// 两种局面并排各量一次，它就从假象变成了读数。
		EditorPanel? editor = main.GetNodeOrNull<EditorPanel>("HUD/HudRoot/EditorPanel");
		bool wasVisible = editor?.Visible ?? false;

		if (editor is not null)
		{
			c.Put("editor_visibility_transferable", editor.SetVisibleForTest(false));

			SnapCamera(main);
			saves.SaveNow();
			Godot.Collections.Dictionary closed = BoardPatternMatch(host, main, path);
			r["panel_closed"] = StripSamples(closed);
			c.Put("board_samples_exist", closed["total"].AsInt32() >= 3);

			// 判据：<b>缩略图与现场帧在同一个投影点上的读数一致</b>。
			//
			// 现场帧当对照，"投影算错"与"缩略图拍错"当场分得开：
			// 前者两张图在投影点上都对不上，后者只有缩略图对不上。
			// 这比"颜色必须等于棋盘底色"更结实 —— 它不依赖我猜对底色常量。
			c.Put("live_frame_matches_board", closed["live_matched"].AsInt32() == closed["total"].AsInt32());
			c.Put("thumbnail_matches_live_frame",
				closed["total"].AsInt32() >= 3
				&& closed["matched"].AsInt32() == closed["live_matched"].AsInt32());

			// 面板开着再抓一张：这里<b>不判产品对错</b>，只把读数带出来。
			editor.SetVisibleForTest(true);
			SnapCamera(main);
			saves.SaveNow();
			Godot.Collections.Dictionary opened = BoardPatternMatch(host, main, path);
			r["panel_open"] = StripSamples(opened);

			// 收干净：钉住可见性这件事必须撤回，否则后面几节会被一个不会隐藏的面板坑到
			editor.AlwaysVisible = false;
			editor.SetVisibleForTest(wasVisible);
			r["editor_visible_after_probe"] = editor.Visible;

			// 回到"面板关着"那一张：这才是存档缩略图应该长什么样
			editor.SetVisibleForTest(false);
			SnapCamera(main);
			saves.SaveNow();
			editor.AlwaysVisible = false;
			editor.SetVisibleForTest(wasVisible);
		}

		c.Put("editor_visibility_restored", (editor?.Visible ?? wasVisible) == wasVisible);

		SnapCamera(main);
		thumb.Show(path);
		r["thumbnail_size"] = thumb.TextureSize;
		c.Put("thumbnail_is_320x180", thumb.TextureSize == new Vector2I(320, 180));

		// 顶栏那一条必须<b>不是</b>棋盘格 —— 反过来说明这张图不是"棋盘铺满"的假图。
		//
		// 容差只能是 0.03：HUD 顶栏底色 <c>#242931</c> 与棋盘格浅色 <c>#2e3440</c>
		// 的距离只有 <c>0.0465</c>，写 0.06 会把顶栏整条认成棋盘格。
		Image? fresh = LoadImage(path);
		if (fresh is not null && fresh.GetWidth() == 320 && fresh.GetHeight() == 180)
		{
			var topBand = new List<bool>();
			for (int x = 8; x < 312; x += 48)
			{
				Color got = fresh.GetPixel(x, 6);
				topBand.Add(ColorDistance(got, main.Board.Theme.BackgroundColor) < PatternTolerance
					|| ColorDistance(got, new Color("#2e3440")) < PatternTolerance);
			}

			int hits = 0;
			foreach (bool b in topBand)
			{
				if (b)
					hits++;
			}

			r["top_band_board_hits"] = hits;
			r["top_band_samples"] = topBand.Count;
			c.Put("thumbnail_top_band_is_not_board", topBand.Count >= 4 && hits == 0);
		}

		// ---------------------------------------------------------- 3. 换一张图要跟着换
		//
		// <b>为什么不用"删掉再存一次、比 mtime"：</b>两次保存可能落在同一秒
		// （<c>FileAccess.GetModifiedTime</c> 是秒级的），于是 mtime 根本没变、
		// 重载不发生 —— 而那是<b>判据不可靠</b>，不是产品有问题。
		// 这里改成写一张尺寸不同的图，把"重载"这件事变成必然可判定的。
		string synthetic = $"{AppPaths.ThumbsDir(saves.CurrentSave)}/自检_thumb_probe.png";
		bool wroteSynthetic = WriteSyntheticThumbnail(synthetic, 64, 36);
		c.Put("probe_wrote_a_synthetic_thumbnail", wroteSynthetic);

		if (wroteSynthetic)
		{
			thumb.Clear();
			thumb.Show(synthetic);
			Vector2I smallSize = thumb.TextureSize;
			ulong mtimeFirst = FileAccess.FileExists(synthetic) ? FileAccess.GetModifiedTime(synthetic) : 0;

			// 覆盖成另一张尺寸不同的图（mtime 也必然不同）
			bool wroteSecond = WriteSyntheticThumbnail(synthetic, 32, 18);
			ulong mtimeSecond = FileAccess.FileExists(synthetic) ? FileAccess.GetModifiedTime(synthetic) : 0;

			thumb.Show(synthetic);

			r["synthetic_first_size"] = smallSize;
			r["synthetic_first_mtime"] = mtimeFirst;
			r["synthetic_second_written"] = wroteSecond;
			r["synthetic_second_mtime"] = mtimeSecond;
			r["reload_count"] = thumb.ReloadCount;
			r["reloaded_size"] = thumb.TextureSize;
			c.Put("thumbnail_reloads_on_content_change",
				smallSize == new Vector2I(64, 36) && thumb.TextureSize == new Vector2I(32, 18)
				&& thumb.ReloadCount > 0);

			DirAccess.RemoveAbsolute(AppPaths.ToAbsolute(synthetic));
		}

		// 收干净：把预览恢复到"刚存下来的那张真图"，后面的菜单那一组才有东西可预览。
		thumb.Show(path);
		thumb.Hide();
		r["loaded_path_before_menu"] = thumb.LoadedPath;
		r["has_texture_before_menu"] = thumb.HasTexture;

		// ---------------------------------------------------------- 4. 菜单悬停
		//
		// 悬停信号是 <c>PopupMenu.IdFocused</c>；自检按项目惯例程序化触发
		// （<c>PopupMenu</c> 是 <c>Window</c>，真弹出来会抢走后续合成鼠标事件）。
		saves.Refresh();
		IReadOnlyList<string> listed = saves.ListedForTest;

		if (listed.Count == 0)
		{
			c.Skip("列不出任何存档，菜单悬停这一组无从测起");
			r["pass"] = c.AllPass();
			return r;
		}

		// 菜单项 id 的实况（自检里读出来的）：<c>[900,-1,1,2,-1,500,501,502,-1,503,504]</c> ——
		// <b>分隔线也占一项</b>（id = -1），所以菜单第 0 项是「当前：xxx」、
		// 第 1 项是分隔线，存档从第 <b>2</b> 项起，且第 2 项的 id 是 <c>1</c>（= <c>SaveBase + 0</c>）。
		// 于是 <c>_listed</c> 的下标 i 对应菜单第 <c>i + 2</c> 项。
		//
		// <b>悬停那一组连红三轮，根因就在这里：</b>第一版写的是 <c>i + 1</c>，
		// 以为"「当前」是第 0 项、存档从第 1 项起" —— 于是实际悬停的是<b>另一个存档</b>，
		// 那个存档没有缩略图，所以"什么都没显示"其实完全正确。
		// 三轮里我都在推"信号到没到"，而报告里没有任何一项能回答它 —— 下面这一项就是补上的。
		int saveItemIndex = IndexOf(listed, saves.CurrentSave);
		int menuIndex = saveItemIndex + 2;
		c.Put("current_save_is_listed",
			saveItemIndex >= 0 && menuIndex < menu.ItemCount && menu.GetItemId(menuIndex) >= 1);

		// 把菜单每一项的 id 读出来，别靠"第几项就是存档"这种推算。
		//
		// 悬停这一组连红了几轮，每一轮我都在推"信号有没有到"，
		// 而报告里<b>没有一项能回答它</b> —— 这就是"把猜换成读"该用的地方。
		var itemIds = new Godot.Collections.Array();
		for (int i = 0; i < menu.ItemCount; i++)
			itemIds.Add(menu.GetItemId(i));
		r["menu_item_ids"] = itemIds;
		r["save_item_index"] = saveItemIndex;
		r["menu_item_count"] = menu.ItemCount;
		r["listed_count"] = listed.Count;

		if (saveItemIndex >= 0)
		{
			// 先按面板那套规则摆一次框子。
			//
			// <b>为什么要显式摆：</b>真实路径是"菜单弹出 → 面板摆框 → 悬停显示"，
			// 而自检里菜单从来没有真的 <c>Popup()</c> 过（它是 <c>Window</c>，
			// 真弹会抢走后续合成鼠标事件），<c>_menu.Position</c> 一直是 (0,0) ——
			// 于是"框子压没压住菜单"这件事必须在一个<b>真实菜单位置</b>上量。
			menu.Position = new Vector2I(90, 52);
			thumb.PlaceForTest(new Vector2(menu.Position.X, menu.Position.Y + 250f), path);

			// <b>发的是"项 id"，不是"项下标"。</b>
			//
			// 这一条是读出来才明白的：菜单项的 id 与下标<b>并不一致</b>
			// （实测 <c>menu_item_ids = [900,-1,1,2,-1,500,...]</c>，而对同一个下标
			// <c>GetItemId(3)</c> 给 2）—— 我先前一直把下标当成 id 发，
			// 于是回调收到的 id 落在"另一个存档"上，什么都没有显示。
			// 报告里 <c>last_focused_id=3</c> 与 <c>menu_id_at_index=2</c> 并排，
			// 一眼就把这件事说清楚了（这就是加那两个只读字段的用处）。
			long menuItemId = menu.GetItemId(menuIndex);
			menu.EmitSignal(PopupMenu.SignalName.IdFocused, menuItemId);

			r["focus_events"] = saves.FocusEvents;
			r["last_focused_id"] = saves.LastFocusedId;
			r["menu_index_emitted"] = menuIndex;
			r["menu_id_emitted"] = menuItemId;
			r["previewed_save"] = saves.PreviewedSaveForTest;
			r["thumbnail_visible_after_hover"] = thumb.Visible;
			r["loaded_path_after_hover"] = thumb.LoadedPath;

			c.Put("hovering_a_save_shows_the_preview",
				thumb.Visible && saves.PreviewedSaveForTest == saves.CurrentSave);
			c.Put("preview_loaded_an_image", thumb.HasTexture);

			// 探针自己的收尾必须在<b>取完证之后</b>：先把诊断落进报告再收。
			// 第一版把 r["previewed_save"] 写在收尾之后，于是它恒为空串 ——
			// 与"回调没被调用"长得一模一样，白看了一轮。

			// 预览框不许压在菜单上（"谁压住了谁"那条 M5.5 的教训）。
			var menuSize = menu.GetContentsMinimumSize();
			if (menuSize.X <= 0f || menuSize.Y <= 0f)
				menuSize = new Vector2(280f, 240f);

			var menuRect = new Rect2(menu.Position, menuSize);
			Rect2 thumbRect = thumb.GetGlobalRect();
			r["menu_rect"] = Rect2ToDict(menuRect);
			r["thumbnail_rect"] = Rect2ToDict(thumbRect);
			c.Put("preview_does_not_cover_the_menu", !thumbRect.Intersects(menuRect));

			// 视口内（框子被算到屏幕外面去过 —— 那是"看不见"的一种）
			Vector2 menuViewport = main.Camera.GetViewportRect().Size;
			c.Put("preview_is_inside_the_viewport",
				thumbRect.Position.X >= 0f && thumbRect.Position.Y >= 0f
				&& thumbRect.End.X <= menuViewport.X && thumbRect.End.Y <= menuViewport.Y);

			// 悬停到"非存档"那一项 → 收起来。
			//
			// 发 <b>-1</b>：菜单里的分隔线 id 就是 -1（见 <c>menu_item_ids</c>）。
			// 第一版发的是 1，而那<b>正是第一个存档项的 id</b>（存档 id 从 1 起），
			// 于是它"正确地"显示了预览，断言却红着 —— 又是一个 id 与下标混淆。
			menu.EmitSignal(PopupMenu.SignalName.IdFocused, -1);
			c.Put("hovering_a_non_save_hides_the_preview", !thumb.Visible);

			// 关菜单 → 也收起来
			menu.EmitSignal(PopupMenu.SignalName.IdFocused, menuIndex);
			menu.Hide();
			c.Put("closing_the_menu_hides_the_preview", !thumb.Visible);

			// 探针不许改变被测对象的可观测状态（M4 的规矩）：把预览收干净。
			thumb.Clear();
			saves.ReleasePreviewForTest();
		}

		r["pass"] = c.AllPass();
		return r;
	}

	/// <summary>
	/// 按"相机自己的换算"把桌面上的取样点投到缩略图里，数一数有多少个落在棋盘格上。
	///
	/// <b>同时读现场帧的同一像素</b>：这是这一节最要紧的一处设计。
	/// 只用缩略图一个读数的话，"投影算错了"与"缩略图拍错了"长得一模一样，
	/// 而它们的处置完全相反（前者改探针、后者改产品）。
	/// 现场帧当对照，两者当场分得开。
	/// </summary>
	private static Godot.Collections.Dictionary BoardPatternMatch(Node host, Main main, string path)
	{
		var result = new Godot.Collections.Dictionary
		{
			["matched"] = 0,
			["live_matched"] = 0,
			["total"] = 0,
		};

		Image? image = LoadImage(path);
		if (image is null || image.GetWidth() != 320 || image.GetHeight() != 180 || main.Camera is null)
			return result;

		Vector2 viewport = main.Camera.GetViewportRect().Size;
		Rect2 board = main.Board.Theme.BoardRect;
		Color dark = main.Board.Theme.BackgroundColor;
		Color light = new Color("#2e3440");

		result["viewport"] = Vec2ToArray(viewport);
		result["board_rect"] = Rect2ToDict(board);
		result["board_dark"] = dark.ToHtml(true);
		result["board_light"] = light.ToHtml(true);
		result["zoom_target"] = main.Camera.ZoomLevel;
		result["zoom_render"] = main.Camera.RenderedZoom;

		Image? live = host.GetViewport()?.GetTexture()?.GetImage();

		int total = 0;
		int matched = 0;
		int liveMatched = 0;
		var rows = new Godot.Collections.Array();
		var liveHexes = new Godot.Collections.Array();

		// 从"相机正看着的那个点"开始<b>向外扫</b>，挑一块真正空着的棋盘区来取样。
		//
		// 这一节的第五版才做对这件事：前几版按固定偏移取样，而这一节跑在整桌探针
		// 折腾完之后 —— 桌上三十多件东西，固定偏移<b>大概率落在牌上</b>。
		// 读数就变成了"牌的颜色 vs 棋盘底色"，红出来的名字却像"缩略图拍错了"。
		// 判据是"棋盘格两色"这件事没变，变的是<b>先确认取样点真的是空地</b>。
		Vector2 center = main.Camera.GetScreenCenterPosition();
		result["screen_center_world"] = Vec2ToArray(center);

		foreach (Vector2 candidate in BoardSampleCandidates(center))
		{
			Vector2 world = candidate;
			if (!board.HasPoint(world))
				continue;

			Vector2 screen = main.Camera.WorldToScreen(world);
			if (screen.X < 0f || screen.Y < 0f || screen.X >= viewport.X || screen.Y >= viewport.Y)
				continue;

			int x = Mathf.Clamp((int)(screen.X / viewport.X * 320f), 0, 319);
			int y = Mathf.Clamp((int)(screen.Y / viewport.Y * 180f), 0, 179);

			Color got = image.GetPixel(x, y);
			bool okThumb = OnPattern(got, dark, light);
			bool okLive = false;
			Color liveColor = Colors.Black;

			if (live is not null && screen.X < live.GetWidth() && screen.Y < live.GetHeight())
			{
				liveColor = live.GetPixel((int)screen.X, (int)screen.Y);
				okLive = OnPattern(liveColor, dark, light);
			}

			// 只统计<b>现场帧确实是棋盘格</b>的那些点：现场帧是"真理"，
			// 拿它当筛选器，缩略图才是在被考的那一个。
			if (!okLive)
				continue;

			total++;
			if (okThumb)
				matched++;

			liveHexes.Add($"{screen.X:0},{screen.Y:0}={liveColor.ToHtml(true)}");
			rows.Add(new Godot.Collections.Dictionary
			{
				["world"] = Vec2ToArray(world),
				["screen"] = Vec2ToArray(screen),
				["thumb_hex"] = got.ToHtml(true),
				["live_hex"] = liveColor.ToHtml(true),
				["thumb_ok"] = okThumb,
			});
		}

		// liveMatched 在这里退化成"筛出来的点数"，保留它是为了让报告读起来一致：
		// 现场帧那一侧恒等于 total（筛的就是它）。
		liveMatched = total;
		result["live_hexes"] = liveHexes;

		result["samples"] = rows;
		result["matched"] = matched;
		result["live_matched"] = liveMatched;
		result["total"] = total;
		return result;
	}

	/// <summary>报告里只留计数与相机状态，不留逐点明细（明细会把报告撑得很难读）。</summary>
	private static Godot.Collections.Dictionary StripSamples(Godot.Collections.Dictionary source)
	{
		var copy = new Godot.Collections.Dictionary();
		foreach (KeyValuePair<Variant, Variant> kv in source)
		{
			if (kv.Key.AsString() != "samples")
				copy[kv.Key] = kv.Value;
		}

		return copy;
	}

	private static bool OnPattern(Color got, Color dark, Color light) =>
		ColorDistance(got, dark) < PatternTolerance || ColorDistance(got, light) < PatternTolerance;

	/// <summary>
	/// 从相机中心向外螺旋铺一批候选取样点。
	///
	/// 步长取 <b>24 世界单位</b>：棋盘格的一格是 120，视口里一个棋盘格约 62 屏幕像素，
	/// 而 24 世界单位 ≈ 12 屏幕像素 —— 于是一圈候选里必然有几个落在纯色格内部，
	/// 而不是压在格线上。步长再大就会稳定地踩到格线附近，颜色介于两色之间。
	/// </summary>
	private static IEnumerable<Vector2> BoardSampleCandidates(Vector2 center)
	{
		const float Step = 24f;

		yield return center;

		for (int ring = 1; ring <= 14; ring++)
		{
			float radius = ring * Step;
			int sides = ring * 4;

			for (int i = 0; i < sides; i++)
			{
				float angle = Mathf.Tau * i / sides;
				yield return center + (new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
			}
		}
	}

	/// <summary>
	/// 抓图之前把相机吸附到目标值。
	///
	/// 相机的"目标值"与"渲染值"是分开的，平滑还在逼近时两者不相等
	/// （<c>BoardCamera</c> 的类注释就是讲这件事）。探针夹在两次处理之间取值，
	/// 不吸附的话投影与已画进图里的位置会差一截 ——
	/// 那一截看起来像"缩略图拍错了"，而其实只是相位不同。
	/// </summary>
	private static void SnapCamera(Main main)
	{
		main.Camera?.SnapToTargets();
		main.Camera?.ForceUpdateTransform();
	}

	/// <summary>
	/// 写一张纯色的小 PNG 到指定路径（探针自己造的图，跑完删掉）。
	///
	/// 用纯色而不是截图：这里要验的是"文件内容变了 → 预览跟着变"，
	/// 内容是什么无关紧要，而"尺寸不同"让重载结果可判定。
	/// </summary>
	private static bool WriteSyntheticThumbnail(string path, int width, int height)
	{
		var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
		image.Fill(new Color("#a3be8c"));
		return image.SavePng(path) == Error.Ok;
	}

	private static Dictionary<string, ulong> SnapshotThumbs()
	{
		var map = new Dictionary<string, ulong>();
		foreach (string name in AppPaths.ListSaveNames())
		{
			string path = $"{AppPaths.ThumbsDir(name)}/board.png";
			if (FileAccess.FileExists(path))
				map[path] = FileAccess.GetModifiedTime(path);
		}

		return map;
	}

	private static Image? LoadImage(string path) =>
		path.Length == 0 || !FileAccess.FileExists(path) ? null : Image.LoadFromFile(path);

	/// <summary>归一化 RGB 距离（0 = 完全相同，1 = 最远）。与 <c>DevReport</c> 口径一致。</summary>
	private static float ColorDistance(Color a, Color b)
	{
		float dr = a.R - b.R;
		float dg = a.G - b.G;
		float db = a.B - b.B;
		return Mathf.Sqrt(((dr * dr) + (dg * dg) + (db * db)) / 3f);
	}

	/// <summary><c>IReadOnlyList</c> 上没有 <c>IndexOf</c>，自己走一遍（列表只有几个元素）。</summary>
	private static int IndexOf(IReadOnlyList<string> list, string value)
	{
		for (int i = 0; i < list.Count; i++)
		{
			if (list[i] == value)
				return i;
		}

		return -1;
	}

	private static Godot.Collections.Array Vec2ToArray(Vector2 v) =>
		new() { Mathf.Round(v.X * 10f) / 10f, Mathf.Round(v.Y * 10f) / 10f };

	private static Godot.Collections.Dictionary Rect2ToDict(Rect2 r) => new()
	{
		["x"] = r.Position.X,
		["y"] = r.Position.Y,
		["w"] = r.Size.X,
		["h"] = r.Size.Y,
	};
}

using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using TabletopSimulator.Core;
using TabletopSimulator.Core.Objects;

namespace TabletopSimulator.Dev;

/// <summary>
/// 物件交互的端到端断言。全部走真实输入管线（<c>Input.ParseInputEvent</c>），
/// 因此验证的是「输入路由 → 物件管理器 → 节点状态」整条链路，而不只是某个函数。
///
/// 覆盖：拖拽位移、步进旋转、翻面、堆叠成形、框选、点骰子即掷。
/// </summary>
internal static class DevObjectSim
{
	internal static async Task<Godot.Collections.Dictionary> Probe(
		Node host, BoardCamera cam, ViewportController vc, ObjectManager objects, ZoneManager? zones = null)
	{
		var r = new Godot.Collections.Dictionary();
		r["object_count_before"] = objects.ObjectCount;

		if (objects.ObjectCount == 0)
		{
			r["skipped"] = "桌面上没有物件";
			return r;
		}

		// "没条件跑"和"跑挂了"必须能区分开：这里返回的字典里<b>刻意不写 pass</b>，
		// 读报告的人看到"没有 pass"就知道这一节根本没执行。
		// 近景截图（--zoom 1）下桌面大部分在视口外，正是这种情况。
		int looseOnScreen = DevInputSim.CountLooseOnScreen(objects, cam);
		r["loose_on_screen"] = looseOnScreen;

		if (looseOnScreen < 2)
		{
			r["skipped"] = $"屏幕内的散件只有 {looseOnScreen} 个，而本节需要至少 2 个当靶子"
				+ "（拖拽 / 堆叠 / 框选都以此为前提）。多半是视角被放大到只看得到桌面一角 —— "
				+ "请用默认的整桌视角重跑，或把 --center 指到有散件的地方。";
			return r;
		}

		// 各步骤互相影响，所以每步都用"按当前状态重新挑目标"的方式，
		// 而不是一开始抓引用 —— 拖拽之后位置就变了。
		await DragToEmptyArea(host, cam, objects, zones, r);
		await DragOutOfBoard(host, cam, objects, r);
		await RotateAndFlip(host, cam, vc, objects, r);
		await FormPile(host, cam, objects, r);
		await BoxSelect(host, cam, objects, zones, r);
		await RollDice(host, cam, objects, r);

		r["object_count_after"] = objects.ObjectCount;

		// 拖拽那一步若因"屏幕上找不到干净落点"跳过，就不算这一节失败 ——
		// 否则一次环境问题会被读成产品问题（本项目最忌讳的"测试说谎"）。
		bool dragPartOk = AsBool(r, "drag_ok") || AsBool(r, "drag_skipped");

		// 拖出桌面那条（P3）：找不到落点时会写 drag_out_skipped（一句说明文字）
		// 并<b>不写那三条断言</b> —— 那是"没条件跑"而不是失败。
		//
		// <b>键名必须与下面写入处逐字一致。</b>这里踩过一次：写入处写的是
		// <c>drag_out_warned_before_release</c>（过去式），而判定读的是
		// <c>drag_out_warns_before_release</c>（第三人称）—— 于是 ContainsKey 恒为假，
		// 整条被容错逻辑当成"没跑"静默跳过。<b>后果是回退验证时它不红</b>：
		// 把"拖出桌面即删除"关掉之后所有断言照样全绿，等于这条功能没有裁判。
		// 这与 M4 那次"聚合判定与子项写入的键名不一致"是同一个坑，只是这次是第三回。
		bool dragOutRan = r.ContainsKey("drag_out_warned_before_release")
			&& r.ContainsKey("drag_out_deletes_the_object")
			&& r.ContainsKey("drag_out_tint_cleared");

		bool dragOutPartOk = dragOutRan
			? AsBool(r, "drag_out_warned_before_release")
				&& AsBool(r, "drag_out_deletes_the_object")
				&& AsBool(r, "drag_out_tint_cleared")
			: true;   // 没跑 = 不计入失败

		r["pass"] = dragPartOk && dragOutPartOk && AsBool(r, "rotate_ok") && AsBool(r, "flip_ok")
			&& AsBool(r, "pile_ok") && AsBool(r, "box_select_ok") && AsBool(r, "dice_ok");

		// 逐项写出来：聚合判定为假而所有子项都是真时，光看布尔值没法定位 ——
		// 而本节偏偏就有过"名字像断言的数据字段把聚合判成失败"这种事。
		r["pass_terms"] = $"drag={dragPartOk} dragOut={dragOutPartOk} "
			+ $"rotate={AsBool(r, "rotate_ok")} flip={AsBool(r, "flip_ok")} "
			+ $"pile={AsBool(r, "pile_ok")} box={AsBool(r, "box_select_ok")} dice={AsBool(r, "dice_ok")}";

		return r;
	}

	// ------------------------------------------------------------------ 1. 拖拽

	/// <summary>把一张散件卡拖到真空中，断言位移精确等于「屏幕位移 / 缩放」。</summary>
	private static async Task DragToEmptyArea(
		Node host, BoardCamera cam, ObjectManager objects, ZoneManager? zones, Godot.Collections.Dictionary r)
	{
		CardObject? card = FindLooseCard(objects, cam);
		if (card is null)
		{
			r["drag_ok"] = false;
			r["drag_note"] = "找不到散件卡";
			return;
		}

		(Vector2? emptyScreen, float clearance, int candidates) =
			DevInputSim.FindEmptiestScreenPoint(cam, objects, zones, DevInputSim.FindBoard(host));

		// 找不到干净落点 = 没条件跑，不是产品坏了。按项目约定报成"跳过"。
		if (emptyScreen is not Vector2 emptyPoint)
		{
			r["drag_ok"] = false;
			r["drag_skipped"] = true;
			r["drag_note"] = $"屏幕上没有既无物件、又不被 HUD 压住的落点（候选 {candidates} 个）—— 跳过拖拽判定";
			return;
		}

		Vector2 target = cam.ScreenToWorld(emptyPoint);

		// 把"这个落点到底有多空"写进报告。它是这条断言的可信度依据：
		// 间隙接近 0 就说明落点其实贴着别的物件，"位移精确等于鼠标位移"即使通过也说明不了什么。
		r["drag_drop_clearance_px"] = clearance;
		Vector2 startPos = card.Position;
		Vector2 startScreen = cam.WorldToScreen(startPos);

		Vector2 screenDelta = (target - startPos) * cam.ZoomLevel;
		if (screenDelta.Length() < 80f)
		{
			r["drag_ok"] = false;
			r["drag_note"] = "空位太近，不足以验证拖拽";
			return;
		}

		PushDrag(host, startScreen, screenDelta);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

		Vector2 moved = card.Position - startPos;
		Vector2 expected = screenDelta / cam.ZoomLevel;

		r["drag_moved"] = Vec2(moved);
		r["drag_expected"] = Vec2(expected);
		r["drag_error_px"] = moved.DistanceTo(expected);
		r["drag_ok"] = moved.DistanceTo(expected) < 1.5f;
	}

	// ------------------------------------------------------------------ 1b. 拖出桌面（M5.5 P3）

	/// <summary>
	/// 「拖到画布外的东西就被删除」—— 用户实测反馈第 5 条，也是他拍板的方案。
	///
	/// <b>三条判据，各自冲着一种会出错的地方：</b>
	/// <list type="number">
	/// <item><b>提前变色</b> —— 松手之前就必须看得出"这一下会删"。
	///   误删的代价是"一次误拖花十分钟收拾"，所以结果要在手还按着的时候可见。
	///   这一条只有在<b>没松手</b>时读得到，所以拖拽刻意拆成"按下 → 移动 →（读）→ 松手"。</item>
	/// <item><b>松手真的删掉</b> —— 物件数 -1、且被删的是刚才那张。</item>
	/// <item><b>反例：拖到桌面内不删</b> —— 没有这一条的话，"什么都删"也能让上一条通过，
	///   而那显然不是用户要的。</item>
	/// </list>
	/// </summary>
	private static async Task DragOutOfBoard(
		Node host, BoardCamera cam, ObjectManager objects, Godot.Collections.Dictionary r)
	{
		Board? board = DevInputSim.FindBoard(host);
		if (board is null)
		{
			r["drag_out_skipped"] = "取不到 Board，跳过";
			return;
		}

		Rect2 boardRect = board.BoardRect;

		// 找一个"在桌面外、且在屏幕上没被 HUD 压住"的落点。
		// 两次判定缺一不可：只判"桌外"可能落在顶栏底下（合成点击会被控件吃掉），
		// 只判"没被压住"可能落在桌面里（那就不是这一条要验的事了）。
		Vector2? outside = FindBoardSideScreenPoint(cam, boardRect, wantInside: false);
		if (outside is not Vector2 outScreen)
		{
			r["drag_out_skipped"] = "屏幕上找不到既在桌面外、又没被 HUD 压住的落点";
			return;
		}

		CardObject? card = FindLooseCard(objects, cam);
		if (card is null)
		{
			r["drag_out_skipped"] = "找不到散件卡";
			return;
		}

		string victimUid = card.Uid;
		int before = objects.ObjectCount;
		Vector2 start = cam.WorldToScreen(card.Position);
		Vector2 delta = outScreen - start;

		r["drag_out_target_screen"] = Vec2(outScreen);
		r["drag_out_target_world"] = Vec2(cam.ScreenToWorld(outScreen));

		// 前置数据：这一条<b>不是断言</b>（名字里刻意不带 _ok）。
		// 第一版它叫 drag_out_world_inside_board —— 那个"期望为 false"的名字
		// 被本节的聚合判定当成了失败，凭空多出一条红。
		r["drag_out_world_off_board"] = !boardRect.HasPoint(cam.ScreenToWorld(outScreen));

		if (delta.Length() < 80f)
		{
			r["drag_out_skipped"] = "落点离靶子太近，不足以验证拖拽";
			return;
		}

		// ---- 按下 + 移动，但<b>先不松手</b>：读"提前变色" ----
		var first = new Vector2(Mathf.Sign(delta.X) * 20f, 0f);
		if (Mathf.Abs(delta.X) < 25f)
			first = delta * 0.5f;

		DevInputSim.PushButton(start, MouseButton.Left, true);
		DevInputSim.PushMotion(start + first, first);
		DevInputSim.PushMotion(outScreen, delta - first);
		await DevInputSim.Frame(host);

		r["drag_out_warned_before_release"] = objects.DragOutWarned;
		r["drag_out_tint"] = Vec2(new Vector2(card.SelfModulate.R, card.SelfModulate.G));

		// ---- 松手：应当被删除 ----
		DevInputSim.PushButton(outScreen, MouseButton.Left, false);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

		r["drag_out_count_before"] = before;
		r["drag_out_count_after"] = objects.ObjectCount;

		bool victimGone = true;
		foreach (TabletopObject o in objects.AllObjects)
		{
			if (o.Uid == victimUid)
				victimGone = false;
		}

		r["drag_out_deletes_the_object"] = objects.ObjectCount == before - 1 && victimGone;

		// 提示色必须收干净 —— 不收的话那张牌（或它的替身）会一直红着。
		r["drag_out_tint_cleared"] = !objects.DragOutWarned;

		// ---- 反例：拖到桌面内不删 ----
		Vector2? inside = FindBoardSideScreenPoint(cam, boardRect, wantInside: true);
		CardObject? card2 = FindLooseCard(objects, cam);

		if (inside is not Vector2 inScreen || card2 is null)
		{
			r["drag_in_skipped"] = "找不到桌面内的干净落点或第二张散件卡，跳过反例";
			return;
		}

		int beforeIn = objects.ObjectCount;
		Vector2 startIn = cam.WorldToScreen(card2.Position);
		PushDrag(host, startIn, inScreen - startIn);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

		r["drag_in_count_before"] = beforeIn;
		r["drag_in_count_after"] = objects.ObjectCount;
		r["drag_inside_board_does_not_delete"] = objects.ObjectCount == beforeIn;
	}

	/// <summary>
	/// 在桌面上找一个屏幕点：<paramref name="wantInside"/> 为真时点在桌面<b>内</b>、
	/// 为假时点在桌面<b>外</b>，两种都要"没被 HUD 控件压住"。
	///
	/// 从桌面中心朝八个方向往外扫，先取最近的那个合条件的点 ——
	/// 刻意不要"屏幕边缘"那种极端位置：物件的读取、选中判定在边缘更容易受
	/// 布局细节影响，而这一节要验的是边界语义，不是边缘情况。
	/// </summary>
	private static Vector2? FindBoardSideScreenPoint(BoardCamera cam, Rect2 boardRect, bool wantInside)
	{
		Vector2 viewport = cam.GetViewportRect().Size;
		List<Rect2> bands = DevInputSim.ScreenBlockerBands(cam);
		Vector2 centerWorld = boardRect.GetCenter();

		Vector2[] directions =
		{
			new(1f, 0f), new(-1f, 0f), new(0f, 1f), new(0f, -1f),
			new(0.71f, 0.71f), new(-0.71f, 0.71f), new(0.71f, -0.71f), new(-0.71f, -0.71f),
		};

		for (float distance = 60f; distance <= 2400f; distance += 60f)
		{
			foreach (Vector2 dir in directions)
			{
				Vector2 world = centerWorld + (dir * distance);

				if (boardRect.HasPoint(world) != wantInside)
					continue;

				Vector2 screen = cam.WorldToScreen(world);

				// 必须在视口内且避开 HUD（顶栏 / 底栏 / 面板）
				if (screen.X < 40f || screen.X > viewport.X - 40f
					|| screen.Y < 40f || screen.Y > viewport.Y - 40f)
					continue;

				if (DevInputSim.IsScreenPointBlockedByControl(bands, screen, 0f))
					continue;

				return screen;
			}
		}

		return null;
	}

	// ------------------------------------------------------------------ 2. 旋转 / 翻面

	private static async Task RotateAndFlip(
		Node host, BoardCamera cam, ViewportController vc, ObjectManager objects, Godot.Collections.Dictionary r)
	{
		CardObject? card = FindLooseCard(objects, cam);
		if (card is null)
		{
			r["rotate_ok"] = false;
			r["flip_ok"] = false;
			return;
		}

		SelectOnly(objects, card);

		// <b>还要把鼠标悬停到这张牌上。</b>
		//
		// 这一步是 P3 之后补上的，而它修掉的是一类<b>"时红时绿"的老假红</b>：
		// 按键作用的<b>目标</b>按"悬停优先 → 悬停对象在选中集里则作用于整个选中集 →
		// 否则选中集"解析（见 ObjectManager.ResolveActionTargets）。而本探针此前
		// <b>只设了选中、没设悬停</b>，于是"悬停指向谁"完全取决于上一步留下的残影 ——
		// 上一步刚好把光标停在靶子上时这一条绿，停在别的牌上时这一条红，
		// 而报告里只有一个 <c>rotation_delta = 0</c>。
		//
		// 是本轮新加的"拖出桌面"那一步把残影挪到了别处，才让这个老问题稳定复现出来
		// （诊断读到 <c>rotate_card_uid = card-0022</c> 而 <c>rotate_hovered_uid = card-0016</c>）。
		// 处置与 M4/M5 那几条一致：<b>探针要把自己的前置条件摆到位</b>，
		// 而不是依赖上一步碰巧留下的状态。
		await Hover(host, cam, card);

		// 诊断：这台"按键不起作用"的现场到底长什么样。
		// P3 加了一步拖拽之后这一节变红了，而报告里只有 rotation_delta = 0 ——
		// 分不出"没找到靶子""靶子不可交互""键没送到"。这几行把答案直接写出来。
		r["rotate_card_uid"] = card.Uid;
		r["rotate_card_screen"] = Vec2(cam.WorldToScreen(card.Position));
		r["rotate_card_visible"] = card.Visible;
		r["rotate_card_zone"] = card.ZoneId;
		r["rotate_selection_count"] = objects.Selection.Count;

		float before = card.RotationDeg;
		int keysBeforeRotate = vc.KeyEvents;
		DevInputSim.PushKey(Key.E);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);
		float after = card.RotationDeg;

		// 键走到哪一层了：输入路由 / 物件系统 / 编辑器面板各看到几个。
		//
		// <b>名字里刻意不带 "reached_router"，也不要当成断言读。</b>
		// E 与 F 是被 ObjectManager 在 _UnhandledKeyInput 里处理的，而那个阶段
		// 排在 ViewportController._UnhandledInput <b>之前</b> —— 所以路由那个计数
		// 对这两个键本来就是 0，那是正常的。真正的判据是"物件系统看到了几个"。
		// （第一版把它们写成 <c>*_key_reached_router</c>，本节聚合判定于是把
		//   "键被正常处理掉"当成了失败，凭空多出两条红。）
		r["rotate_key_route_events"] = vc.KeyEvents - keysBeforeRotate;
		r["rotate_objects_key_seen"] = objects.KeyEventsSeen;
		r["rotate_editor_key_seen"] = EditorPanel.KeySeenTotal;
		r["rotate_hovered_uid"] = objects.HoveredUidForTest;		r["rotation_before"] = before;
		r["rotation_after"] = after;
		r["rotation_delta"] = after - before;
		r["rotate_ok"] = Mathf.Abs((after - before) - GameConfig.KeyRotateStepDegrees) < 0.01f;

		bool faceBefore = card.IsFaceDown;

		// 按下 F 之前先记一笔"到目前为止有多少个键走到了输入路由"。
		//
		// 这是一个只读计数器（<c>ViewportController.KeyEvents</c>），存在的理由很具体：
		// 这一条曾经时红时绿 —— 同一个二进制、同一段代码，一次 <c>flip_ok=true</c>、
		// 一次 <c>false</c>，而报告里只有一行 false，看不出到底是
		// <b>键没送到输入路由</b>还是<b>送到了、但目标集是空的</b>。
		// 现在两种情况在报告里长得完全不一样：前者 <c>flip_key_reached_router=false</c>，
		// 后者为 true 而 <c>rotation_delta</c> 照样正确。
		int keysBefore = vc.KeyEvents;

		DevInputSim.PushKey(Key.F);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

		r["flip_key_route_events"] = vc.KeyEvents - keysBefore;
		r["face_down_after"] = card.IsFaceDown;
		r["flip_ok"] = card.IsFaceDown != faceBefore;
	}

	// ------------------------------------------------------------------ 3. 堆叠

	/// <summary>把一张散件卡拖到另一张散件卡上，断言两者并成一堆。</summary>
	private static async Task FormPile(Node host, BoardCamera cam, ObjectManager objects, Godot.Collections.Dictionary r)
	{
		(List<CardObject> loose, _) = SplitCards(objects);

		if (loose.Count < 2)
		{
			r["pile_ok"] = false;
			r["pile_note"] = $"散件卡不足（{loose.Count} 张）";
			return;
		}

		CardObject mover = loose[0];
		CardObject anchor = loose[1];

		Vector2 startScreen = cam.WorldToScreen(mover.Position);
		Vector2 screenDelta = (anchor.Position - mover.Position) * cam.ZoomLevel;

		PushDrag(host, startScreen, screenDelta);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

		r["pile_mover_id"] = mover.Uid;
		r["pile_anchor_id"] = anchor.Uid;
		r["pile_mover_pile"] = mover.PileId;
		r["pile_anchor_pile"] = anchor.PileId;
		r["pile_same"] = mover.PileId != 0 && mover.PileId == anchor.PileId;

		int memberCount = 0;
		if (mover.PileId != 0 && objects.Piles.TryGetValue(mover.PileId, out Pile? pile))
			memberCount = pile.Count;

		r["pile_member_count"] = memberCount;
		r["pile_ok"] = mover.PileId != 0 && mover.PileId == anchor.PileId && memberCount >= 2;
	}

	// ------------------------------------------------------------------ 4. 框选

	/// <summary>
	/// 从卡牌旁边的空白处拖出一个罩住它的框，断言：
	/// 选中数变多、且过程中物件<b>没有被拖走</b>（框选绝不该移动物件）。
	/// </summary>
	private static async Task BoxSelect(
		Node host, BoardCamera cam, ObjectManager objects, ZoneManager? zones, Godot.Collections.Dictionary r)
	{
		CardObject? card = FindLooseCard(objects, cam, requireFaceUp: false);
		if (card is null)
		{
			r["box_select_ok"] = false;
			r["box_select_note"] = "找不到散件卡";
			return;
		}

		objects.ClearSelection();
		await DevInputSim.Frame(host);

		Vector2 cardScreen = cam.WorldToScreen(card.Position);
		Vector2? startScreen = DevInputSim.FindEmptyScreenPointNear(cam, objects, cardScreen, 420f, zones);

		if (startScreen is null)
		{
			r["box_select_ok"] = false;
			r["box_select_note"] = "卡牌附近找不到空白起点";
			return;
		}

		Vector2 start = startScreen.Value;
		Vector2 end = cardScreen + new Vector2(12f, 12f);
		Vector2 positionBefore = card.Position;

		// 起点在空白 → 管理器不会建拖拽集，于是这次拖动被识别为框选
		DevInputSim.PushButton(start, MouseButton.Left, true);
		DevInputSim.PushMotion(start + new Vector2(20f, 0f), new Vector2(20f, 0f));
		DevInputSim.PushMotion(end, end - (start + new Vector2(20f, 0f)));
		DevInputSim.PushButton(end, MouseButton.Left, false);

		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

		r["box_select_start"] = Vec2(start);
		r["box_select_end"] = Vec2(end);
		r["box_selected_count"] = objects.Selection.Count;
		r["box_card_moved_px"] = card.Position.DistanceTo(positionBefore);
		r["box_select_ok"] = objects.Selection.Count > 0 && card.Position.DistanceTo(positionBefore) < 0.01f;
	}

	// ------------------------------------------------------------------ 5. 骰子

	private static async Task RollDice(Node host, BoardCamera cam, ObjectManager objects, Godot.Collections.Dictionary r)
	{
		DiceObject? dice = null;
		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (obj is DiceObject d)
			{
				dice = d;
				break;
			}
		}

		if (dice is null)
		{
			r["dice_ok"] = false;
			r["dice_note"] = "桌面上没有骰子";
			return;
		}

		Vector2 screen = cam.WorldToScreen(dice.Position);
		DevInputSim.PushButton(screen, MouseButton.Left, true);
		await DevInputSim.Frame(host);
		DevInputSim.PushButton(screen, MouseButton.Left, false);
		await DevInputSim.Frame(host);

		r["dice_rolling_after_click"] = dice.IsRolling;
		r["dice_sides"] = dice.Sides;
		r["dice_count"] = dice.Count;
		r["dice_ticks_at_click"] = dice.ProcessTicks;
		r["dice_can_process"] = dice.CanProcess();

		// 轮询等它停下来，而不是赌一个固定帧数 —— 帧率在无头/后台运行时会变，
		// 写死帧数会得到"有时候过有时候不过"的假失败。同时记录用了多少帧，本身就是诊断信息。
		int frames = 0;
		const int MaxFrames = 400;
		while (dice.IsRolling && frames < MaxFrames)
		{
			await DevInputSim.Frame(host);
			frames++;
		}

		r["dice_frames_to_settle"] = frames;
		r["dice_ticks_total"] = dice.ProcessTicks;
		r["dice_elapsed_s"] = dice.Elapsed;

		bool inRange = dice.Values.Count == dice.Count;
		foreach (int v in dice.Values)
		{
			if (v < 1 || v > dice.Sides)
				inRange = false;
		}

		r["dice_values"] = ToArray(dice.Values);
		r["dice_total"] = dice.Total;
		r["dice_settled"] = !dice.IsRolling;
		r["dice_values_in_range"] = inRange;
		r["dice_ok"] = dice.SettledOnce && inRange && !dice.IsRolling;
	}

	// ------------------------------------------------------------------ 工具

	/// <summary>
	/// 把鼠标合成地移到某个物件上（只有移动，不点击）。
	///
	/// 需要它是因为"悬停"是物件系统里一个<b>独立于选中</b>的状态，而按键的目标
	/// 按"悬停优先"解析 —— 探针想让按键作用到某张牌上，就必须真的把光标移过去，
	/// 光设选中是不够的（见 <see cref="RotateAndFlip"/> 里那段说明）。
	/// </summary>
	private static async Task Hover(Node host, BoardCamera cam, TabletopObject obj)
	{
		DevInputSim.PushMotion(cam.WorldToScreen(obj.Position), Vector2.Zero);
		await DevInputSim.Frame(host);
	}

	private static void PushDrag(Node host, Vector2 startScreen, Vector2 totalScreenDelta)
	{
		_ = host;

		// 第一段必须越过 5px 拖拽阈值，第二段补足剩下的
		var first = new Vector2(Mathf.Sign(totalScreenDelta.X) * 20f, 0f);
		if (Mathf.Abs(totalScreenDelta.X) < 25f)
			first = totalScreenDelta * 0.5f;

		Vector2 second = totalScreenDelta - first;

		DevInputSim.PushButton(startScreen, MouseButton.Left, true);
		DevInputSim.PushMotion(startScreen + first, first);
		DevInputSim.PushMotion(startScreen + totalScreenDelta, second);
		DevInputSim.PushButton(startScreen + totalScreenDelta, MouseButton.Left, false);
	}

	/// <summary>
	/// 挑一张"散件卡"当靶子。
	/// </summary>
	/// <param name="requireFaceUp">
	/// 是否需要正面朝上。拖拽/旋转/翻面这些步骤要它，因为盖放的牌看不到内容；
	/// 但<b>框选不需要</b> —— 框选只要求"这张牌能被框住"，正面背面都一样。
	/// 曾经因为这里一刀切要求正面，前面几步把仅有的 5 张正面散件消耗光之后
	/// （翻面 1 张、并堆 2 张），框选就在自己的里程碑里莫名其妙地红了。
	/// </param>
	private static CardObject? FindLooseCard(ObjectManager objects, BoardCamera cam, bool requireFaceUp = true)
	{
		for (int i = objects.AllObjects.Count - 1; i >= 0; i--)
		{
			TabletopObject obj = objects.AllObjects[i];

			// 必须挑屏幕上看得见的：前面的步骤可能把物件拖到视口之外，
			// 对它合成点击会落在视口外，得到毫无意义的失败。
			//
			// M3 起还要排除区域成员：牌库最上面那几张牌同样满足 "PileId == 0 && Visible"，
			// 但它们的位置由区域排版决定、还可能在牌库矩形里，拿它们当"散件"去拖
			// 会拖出区域、并让断言测的东西和名字对不上。
			if (obj is CardObject card && card.ZoneId == "" && card.PileId == 0
				&& card.Visible
				&& (!requireFaceUp || !card.IsFaceDown)
				&& DevInputSim.IsOnScreen(cam, card.Position))
				return card;
		}

		return null;
	}

	/// <summary>拆分卡牌：散件（可自由拖动的靶子） vs 已在堆里的。</summary>
	private static (List<CardObject> Loose, List<CardObject> Piled) SplitCards(ObjectManager objects)
	{
		var loose = new List<CardObject>();
		var piled = new List<CardObject>();

		foreach (TabletopObject obj in objects.AllObjects)
		{
			// 区域成员一律不算散件 —— 它们的位置归区域管，不是"能随便拖的靶子"。
			if (obj is not CardObject card || !card.Visible || card.ZoneId != "")
				continue;

			if (card.PileId == 0)
				loose.Add(card);
			else
				piled.Add(card);
		}

		return (loose, piled);
	}

	private static void SelectOnly(ObjectManager objects, TabletopObject obj)
	{
		// 直接用管理器的公共 API —— 别自己拼一个"先全选再清空"的伪实现，
		// 那会顺手把物件删掉。
		objects.SelectOnly(obj);
	}

	private static Godot.Collections.Array Vec2(Vector2 v) =>
		new() { v.X, v.Y };

	private static Godot.Collections.Array ToArray(IReadOnlyList<int> values)
	{
		var arr = new Godot.Collections.Array();
		foreach (int v in values)
			arr.Add(v);
		return arr;
	}

	private static bool AsBool(Godot.Collections.Dictionary dict, string key)
		=> dict.ContainsKey(key) && dict[key].AsBool();
}

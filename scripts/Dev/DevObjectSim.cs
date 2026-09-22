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
		await RotateAndFlip(host, cam, vc, objects, r);
		await FormPile(host, cam, objects, r);
		await BoxSelect(host, cam, objects, zones, r);
		await RollDice(host, cam, objects, r);

		r["object_count_after"] = objects.ObjectCount;

		// 拖拽那一步若因"屏幕上找不到干净落点"跳过，就不算这一节失败 ——
		// 否则一次环境问题会被读成产品问题（本项目最忌讳的"测试说谎"）。
		bool dragPartOk = AsBool(r, "drag_ok") || AsBool(r, "drag_skipped");

		r["pass"] = dragPartOk && AsBool(r, "rotate_ok") && AsBool(r, "flip_ok")
			&& AsBool(r, "pile_ok") && AsBool(r, "box_select_ok") && AsBool(r, "dice_ok");

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

		float before = card.RotationDeg;
		DevInputSim.PushKey(Key.E);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);
		float after = card.RotationDeg;

		r["rotation_before"] = before;
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

		r["flip_key_reached_router"] = vc.KeyEvents > keysBefore;
		r["face_down_before"] = faceBefore;
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

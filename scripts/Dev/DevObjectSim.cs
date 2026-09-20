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
		Node host, BoardCamera cam, ViewportController vc, ObjectManager objects)
	{
		var r = new Godot.Collections.Dictionary();
		r["object_count_before"] = objects.ObjectCount;

		if (objects.ObjectCount == 0)
		{
			r["skipped"] = "桌面上没有物件";
			return r;
		}

		// 各步骤互相影响，所以每步都用"按当前状态重新挑目标"的方式，
		// 而不是一开始抓引用 —— 拖拽之后位置就变了。
		await DragToEmptyArea(host, cam, objects, r);
		await RotateAndFlip(host, cam, objects, r);
		await FormPile(host, cam, objects, r);
		await BoxSelect(host, cam, objects, r);
		await RollDice(host, cam, objects, r);

		r["object_count_after"] = objects.ObjectCount;
		r["pass"] = AsBool(r, "drag_ok") && AsBool(r, "rotate_ok") && AsBool(r, "flip_ok")
			&& AsBool(r, "pile_ok") && AsBool(r, "box_select_ok") && AsBool(r, "dice_ok");

		return r;
	}

	// ------------------------------------------------------------------ 1. 拖拽

	/// <summary>把一张散件卡拖到真空中，断言位移精确等于「屏幕位移 / 缩放」。</summary>
	private static async Task DragToEmptyArea(Node host, BoardCamera cam, ObjectManager objects, Godot.Collections.Dictionary r)
	{
		CardObject? card = FindLooseCard(objects, cam);
		if (card is null)
		{
			r["drag_ok"] = false;
			r["drag_note"] = "找不到散件卡";
			return;
		}

		Vector2 target = cam.ScreenToWorld(DevInputSim.FindEmptyScreenPoint(cam, objects));
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

	private static async Task RotateAndFlip(Node host, BoardCamera cam, ObjectManager objects, Godot.Collections.Dictionary r)
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
		DevInputSim.PushKey(Key.F);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

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
	private static async Task BoxSelect(Node host, BoardCamera cam, ObjectManager objects, Godot.Collections.Dictionary r)
	{
		CardObject? card = FindLooseCard(objects, cam);
		if (card is null)
		{
			r["box_select_ok"] = false;
			r["box_select_note"] = "找不到散件卡";
			return;
		}

		objects.ClearSelection();
		await DevInputSim.Frame(host);

		Vector2 cardScreen = cam.WorldToScreen(card.Position);
		Vector2? startScreen = DevInputSim.FindEmptyScreenPointNear(cam, objects, cardScreen, 420f);

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

	private static CardObject? FindLooseCard(ObjectManager objects, BoardCamera cam)
	{
		for (int i = objects.AllObjects.Count - 1; i >= 0; i--)
		{
			TabletopObject obj = objects.AllObjects[i];

			// 必须挑屏幕上看得见的：前面的步骤可能把物件拖到视口之外，
			// 对它合成点击会落在视口外，得到毫无意义的失败。
			if (obj is CardObject card && card.PileId == 0 && card.Visible && !card.IsFaceDown
				&& DevInputSim.IsOnScreen(cam, card.Position))
				return card;
		}

		return null;
	}

	private static (List<CardObject> Loose, List<CardObject> Piled) SplitCards(ObjectManager objects)
	{
		var loose = new List<CardObject>();
		var piled = new List<CardObject>();

		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (obj is not CardObject card || !card.Visible)
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

using System.Text;
using System.Threading.Tasks;
using Godot;
using TabletopSimulator.Core;
using TabletopSimulator.Core.Objects;

namespace TabletopSimulator.Dev;

/// <summary>
/// 开发期截图自检。只在命令行带 <c>--shot &lt;路径&gt;</c> 时干活；正常运行完全无副作用。
///
/// 为什么要单独做这条路：
/// 走 MCP 的 <c>game_eval</c> 要求游戏窗口位于前台（窗口被切到后台时 Godot 会挂起主循环），
/// 而"自己把游戏拉起来 → 等 N 帧 → 存图 → 退出"完全不需要窗口焦点，
/// 可以在命令行里反复跑。于是每个里程碑都能拿到一张<b>可复现</b>的验收图，
/// 外加一份图像统计（用来断言"确实渲染出了东西"，而不只是看了一眼）。
///
/// 用法：
/// <code>
/// Godot_v4.7.2-stable_mono_win64_console.exe --path &lt;项目&gt; -- ^
///     --shot "D:\tmp\m1.png" --shot-frames 30 --shot-exit
/// </code>
/// 会在 PNG 旁边写一个同名 <c>.meta.json</c>，含分辨率、采样到的不同颜色数、平均亮度。
/// </summary>
[GlobalClass]
public partial class DevCapture : Node
{
	private const string FlagShot = "--shot";
	private const string FlagFrames = "--shot-frames";
	private const string FlagExit = "--shot-exit";
	private const string FlagZoom = "--zoom";
	private const string FlagCenter = "--center";
	private const string FlagOpenLog = "--open-log";

	public override void _Ready()
	{
		// <b>定存档根要排在最前面。</b>
		//
		// Godot 是子节点先 _Ready，所以本节点（DevCapture）的 _Ready 跑在
		// Main._Ready 之前。Main 里也调了一次 AppPaths.Initialize ——
		// 但自检的报告是在**本节点**里生成的，若不在这里先定根，
		// 报告与探针就会用默认的 user:// 根，而自检恰恰是为了避开它。
		// 两次调用是幂等的（同一个命令行参数，同样的结果），不冲突。
		AppPaths.Initialize(CmdLine.GetValue(AppPaths.RootFlag));

		string shotPath = CmdLine.GetValue(FlagShot);
		if (string.IsNullOrWhiteSpace(shotPath))
		{
			SetProcess(false);
			return;
		}

		_ = RunAsync(shotPath, CmdLine.GetInt(FlagFrames, 30), CmdLine.HasFlag(FlagExit));
	}

	/// <summary>
	/// 包一层异常处理。<b>这不是可选的礼貌，是必须的。</b>
	///
	/// <see cref="CaptureAsync"/> 是"发射后不管"启动的（<c>_ = ...</c>），
	/// 它的 <c>Task</c> 没人 await。于是里面任何一处抛异常，都会被静静地吞进
	/// 那个 Task 里：<b>自检既不打日志、也不退出，进程就那么挂到天荒地老</b>。
	/// 排查时看到的现象只有"跑不完"，完全不知道是哪一步炸了 ——
	/// 这一轮就为此白等了一次十分钟的超时。
	///
	/// 所以这里显式接住、打出来、并按 <c>--shot-exit</c> 退出。
	/// </summary>
	private async Task RunAsync(string shotPath, int frames, bool quitAfter)
	{
		try
		{
			await CaptureAsync(shotPath, frames, quitAfter);
		}
		catch (System.Exception e)
		{
			GD.PrintErr($"[DevCapture] 自检抛异常，已中止：{e.GetType().Name}: {e.Message}");
			GD.PrintErr(e.StackTrace ?? "(无堆栈)");

			if (quitAfter)
				GetTree().Quit(2);
		}
	}

	/// <summary>
	/// 截图前覆盖视角。存在的意义：默认的"适配整桌"是 52% 缩放，
	/// 卡面文字在那下面只有十几个像素，根本看不清版式细节。
	/// 用 <c>--zoom 1</c> 出一张 100% 的近景图，才谈得上检查卡面渲染。
	/// </summary>
	private void ApplyViewOverrides()
	{
		string zoomRaw = CmdLine.GetValue(FlagZoom);
		string centerRaw = CmdLine.GetValue(FlagCenter);

		if (string.IsNullOrWhiteSpace(zoomRaw) && string.IsNullOrWhiteSpace(centerRaw))
			return;

		Node? main = GetTree().Root.GetNodeOrNull("Main");
		if (main?.GetNodeOrNull("Camera2D") is not BoardCamera cam)
			return;

		if (float.TryParse(zoomRaw, System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float zoom) && zoom > 0f)
		{
			cam.SetZoomLevel(zoom);
		}

		if (!string.IsNullOrWhiteSpace(centerRaw))
		{
			string[] parts = centerRaw.Split(',');
			if (parts.Length == 2 &&
				float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
					System.Globalization.CultureInfo.InvariantCulture, out float cx) &&
				float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
					System.Globalization.CultureInfo.InvariantCulture, out float cy))
			{
				cam.CenterOn(new Vector2(cx, cy));
			}
		}

		cam.SnapToTargets();
	}

	private async Task CaptureAsync(string shotPath, int frames, bool quitAfter)
	{
		// 先等一帧再覆盖视角：Godot 是子节点先 _Ready，所以 DevCapture._Ready()
		// 跑在 Main._Ready() 之前，那时 Main 还没把初始视角定下来，覆盖会被冲掉。
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		ApplyViewOverrides();

		// --open-log：截一张"操作日志面板打开着"的图。
		// 面板默认是关的（Tab 才开），不这样的话验收图上看不到它 ——
		// 而"面板长什么样"恰恰是这一步唯一需要人眼确认的东西。
		if (CmdLine.HasFlag(FlagOpenLog) &&
			GetTree().Root.GetNodeOrNull("Main") is Main main && GodotObject.IsInstanceValid(main.Log))
		{
			main.Log.Open();
		}

		// 再等若干帧，让镜头定位、主题应用、字体光栅化都落定。
		for (int i = 0; i < Mathf.Max(frames, 1); i++)
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

		Image image = GetViewport().GetTexture().GetImage();
		Error err = image.SavePng(shotPath);

		if (err != Error.Ok)
		{
			GD.PushError($"[DevCapture] 存图失败 {shotPath}：{err}");
		}
		else
		{
			(int distinct, float meanLum) = SampleStats(image);
			WriteMeta(shotPath, image.GetWidth(), image.GetHeight(), distinct, meanLum);

			// 报告在截图之后生成 —— 里面的相机会动镜头，放在后面才不会污染已存下的那一帧。
			await WriteReportAsync(shotPath);
		}

		if (quitAfter)
			GetTree().Quit(err == Error.Ok ? 0 : 1);
	}

	private async Task WriteReportAsync(string shotPath)
	{
		// 注意是场景树根（Window），不是 this —— DevCapture 自己是 Main 的子节点。
		Node root = GetTree().Root;

		// 每个阶段前后都打一行。这些打点不是装饰：自检一旦卡住（死循环、await 永不返回），
		// 没有它们就只能看到"进程不退出"，完全不知道该看哪一段代码。
		// shot.ps1 会把 [DevCapture] 开头的行原样打出来，于是卡在哪一步一眼可见。
		GD.Print("[DevCapture] phase: report build");
		Godot.Collections.Dictionary report = DevReport.Build(root, GetViewport().GetTexture().GetImage());

		// JSON 地基（M4 第 1 步）：不依赖场景树，所以放在 Main 存在性检查之外。
		GD.Print("[DevCapture] phase: json simulation");
		report["json_simulation"] = DevJsonSim.Probe();

		Node? main = root.GetNodeOrNull("Main");
		if (main?.GetNodeOrNull("Camera2D") is BoardCamera cam &&
			main.GetNodeOrNull("ViewportController") is ViewportController vc)
		{
			ObjectManager? objects = main.GetNodeOrNull<ObjectManager>("Objects");
			ZoneManager? zones = main.GetNodeOrNull<ZoneManager>("Zones");
			SceneSnapshot? baseline = null;

			// 日志面板要从 Main 上取（它是代码建的，不在场景文件里）。
			// 取不到时传 null，探针会跳过面板那几条断言而不是崩掉 ——
			// "没条件跑"必须和"跑挂了"长得不一样，这条约定 M3 就定下了。
			LogPanel? log = (main as Main)?.Log;

			// 撤销流程的自检排在<b>最前面</b>：它要从"这一局真实的起步状态"往下走，
			// 而任何先动过手的探针都会把历史重新基准化（见 DevUndoFlowSim 的说明）。
			// 它自己会走快照写回把桌子恢复原样，所以不给后面的探针留脏状态。
			if (objects is not null && zones is not null &&
				main.GetNodeOrNull<UndoSystem>("Undo") is UndoSystem undoAtStart)
			{
				GD.Print("[DevCapture] phase: undo flow simulation");
				report["undo_flow_simulation"] = DevUndoFlowSim.Probe(objects, zones, undoAtStart);
			}

			GD.Print("[DevCapture] phase: input simulation");
			report["input_simulation"] = await DevInputSim.CameraAndPointerProbe(this, cam, vc, objects);

			if (objects is not null)
			{
				// 区域的顺序断言要跑在物件/顺序断言<b>之前</b>：
				// 后两者会大量拖拽、成堆、甚至把物件数翻倍（全选复制），
				// 跑完之后桌面已经不是"一局刚开始"的样子了。
				// 区域那条循环断言的语义是"从开局跑通一局"，所以它得先来。
				if (zones is not null)
				{
					// 开局快照：M4「撤销 = 快照写回」那条断言的比对基准。
					// 必须在任何操作探针动手之前抓。
					GD.Print("[DevCapture] phase: baseline snapshot");
					baseline = DevUndoSim.CaptureBaseline(objects, zones);

					GD.Print("[DevCapture] phase: zone simulation");
					report["zone_simulation"] = await DevZoneSim.Probe(this, cam, objects, zones);
				}
				else
				{
					GD.Print("[DevCapture] phase: zone simulation SKIPPED (Main/Zones 上没有 ZoneManager)");
				}

				GD.Print("[DevCapture] phase: object simulation");
				report["object_simulation"] = await DevObjectSim.Probe(this, cam, vc, objects, zones);

				GD.Print("[DevCapture] phase: sequence simulation");
				report["sequence_simulation"] = await DevSequenceSim.Probe(this, cam, vc, objects, zones);

				// 不变量探针自身的体检放这里：它会短暂注入损坏再还原，
				// 排在这里才能保证后面没有别的探针去读那份被注入过的状态。
				if (zones is not null)
				{
					GD.Print("[DevCapture] phase: invariant probe self-test");
					report["invariant_probe"] = DevZoneSim.VerifyInvariants(objects, zones);
				}

				// 拆桌检查：它是<b>破坏性</b>的（清空所有区域），
				// 放在前面会让后面几节没东西可测 —— 探针之间不该互相拆台。
				if (zones is not null)
				{
					GD.Print("[DevCapture] phase: zone teardown");
					report["zone_teardown"] = await DevZoneSim.VerifyTeardown(this, objects, zones);
				}

				// 快照写回断言排<b>最后</b>：它前面那一百多条断言正好构成了
				// "把桌子折腾乱"的过程，而它要把桌子恢复成开局的样子并逐字段比对。
				// 排在拆桌之后是因为拆桌会清空所有区域 —— 恢复快照正好连区域一起验了。
				if (baseline is not null && zones is not null)
				{
					GD.Print("[DevCapture] phase: snapshot restore");
					report["undo_simulation"] = DevUndoSim.RestoreAndCompare(objects, zones, baseline);
				}

				// 撤销历史（M4 第 3 步）+ 操作日志面板（第 6 步）。
				// 排在快照写回之后：它自己会 Reset 历史、拖牌、撤销，
				// 不依赖前面的桌子状态。
				if (main.GetNodeOrNull<UndoSystem>("Undo") is UndoSystem undo && zones is not null)
				{
					GD.Print("[DevCapture] phase: history simulation");
					report["history_simulation"] = await DevHistorySim.Probe(
						this, cam, objects, zones, undo, log, vc);
				}
			}
		}
		else
		{
			report["input_simulation"] = new Godot.Collections.Dictionary
			{
				["skipped"] = "Main/Camera2D 或 Main/ViewportController 没找到",
			};
		}

		GD.Print("[DevCapture] phase: write json");
		string json = Json.Stringify(report, "  ");

		string reportPath = shotPath + ".report.json";
		using FileAccess? f = FileAccess.Open(reportPath, FileAccess.ModeFlags.Write);
		if (f is null)
		{
			GD.PushError($"[DevCapture] 报告写入失败 {reportPath}");
			return;
		}

		f.StoreString(json);
		GD.Print($"[DevCapture] 自检报告 -> {reportPath}");
	}

	/// <summary>
	/// 稀疏采样统计。用来机械地判断"这一帧确实有内容"：全黑/全纯色的画面
	/// 采样颜色数会接近 1，正常渲染出桌面 + 网格 + UI 则会是几十以上。
	/// </summary>
	private static (int Distinct, float MeanLuminance) SampleStats(Image image)
	{
		var seen = new System.Collections.Generic.HashSet<uint>();
		double luminanceSum = 0d;
		int samples = 0;

		const int step = 6;
		for (int y = 0; y < image.GetHeight(); y += step)
		{
			for (int x = 0; x < image.GetWidth(); x += step)
			{
				Color c = image.GetPixel(x, y);
				seen.Add(c.ToRgba32());
				luminanceSum += (0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B);
				samples++;
			}
		}

		return (seen.Count, samples > 0 ? (float)(luminanceSum / samples) : 0f);
	}

	private static void WriteMeta(string shotPath, int w, int h, int distinct, float meanLum)
	{
		string json = new StringBuilder()
			.Append("{\n")
			.Append($"  \"width\": {w},\n")
			.Append($"  \"height\": {h},\n")
			.Append($"  \"sampled_distinct_colors\": {distinct},\n")
			.Append($"  \"mean_luminance\": {meanLum.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)},\n")
			.Append($"  \"engine\": \"{Engine.GetVersionInfo()["string"]}\"\n")
			.Append("}\n")
			.ToString();

		using FileAccess? f = FileAccess.Open(shotPath + ".meta.json", FileAccess.ModeFlags.Write);
		f?.StoreString(json);
	}
}

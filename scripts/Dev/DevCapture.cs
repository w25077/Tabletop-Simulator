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

	public override void _Ready()
	{
		string shotPath = CmdLine.GetValue(FlagShot);
		if (string.IsNullOrWhiteSpace(shotPath))
		{
			SetProcess(false);
			return;
		}

		_ = CaptureAsync(shotPath, CmdLine.GetInt(FlagFrames, 30), CmdLine.HasFlag(FlagExit));
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
		Godot.Collections.Dictionary report = DevReport.Build(root, GetViewport().GetTexture().GetImage());

		Node? main = root.GetNodeOrNull("Main");
		if (main?.GetNodeOrNull("Camera2D") is BoardCamera cam &&
			main.GetNodeOrNull("ViewportController") is ViewportController vc)
		{
			ObjectManager? objects = main.GetNodeOrNull<ObjectManager>("Objects");

			report["input_simulation"] = await DevInputSim.CameraAndPointerProbe(this, cam, vc, objects);

			if (objects is not null)
			{
				report["object_simulation"] = await DevObjectSim.Probe(this, cam, vc, objects);
				report["sequence_simulation"] = await DevSequenceSim.Probe(this, cam, vc, objects);
			}
		}
		else
		{
			report["input_simulation"] = new Godot.Collections.Dictionary
			{
				["skipped"] = "Main/Camera2D 或 Main/ViewportController 没找到",
			};
		}

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

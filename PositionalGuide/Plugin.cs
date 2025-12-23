using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Numerics;

namespace VariableVixen.PositionalGuide;

public class Plugin: IDalamudPlugin {
	public const string Command = "/posguide";

	private const int CircleSegmentCount = 128;
	// lineIndexToRad and circleSegmentIdxToRad are fixed and only need to be calculated once at the start,
	// since neither the number of lines nor the number of circle segments change
	private readonly float[] lineIndexToAngle = Enumerable.Range(Configuration.IndexFront, Configuration.IndexFrontLeft + 1).Select(idx => (float)(idx * Math.PI / 4)).ToArray();
	private readonly float[] circleSegmentIdxToAngle = Enumerable.Range(0, CircleSegmentCount).Select(idx => (float)(idx * (2.0 * Math.PI / CircleSegmentCount))).ToArray();
	private readonly Vector4[] circleSegmentIdxToColour = new Vector4[CircleSegmentCount];

	private enum CircleTypes { Target, Outer };

	private bool disposed;

	public const string Name = "Positional Assistant";
	public const string DTRDisplayName = "Guidelines";

	[PluginService] public static IGameGui Gui { get; private set; } = null!;
	[PluginService] public static IChatGui Chat { get; private set; } = null!;
	[PluginService] public static IDalamudPluginInterface Interface { get; private set; } = null!;
	[PluginService] public static ICommandManager Commands { get; private set; } = null!;
	[PluginService] public static IClientState Client { get; private set; } = null!;
	[PluginService] public static ITargetManager Targets { get; private set; } = null!;
	[PluginService] public static IPluginLog Log { get; private set; } = null!;
	[PluginService] public static INotificationManager Notifications { get; private set; } = null!;
	[PluginService] public static IObjectTable Objects { get; private set; } = null!;

	public Configuration Config { get; private set; }

	private readonly WindowSystem windowSystem;
	private readonly ConfigWindow configWindow;
	private readonly IDtrBarEntry dtrEntry;

	public Plugin(IDtrBar dtrBar) {
		this.Config = Interface.GetPluginConfig() as Configuration ?? new();
		this.Config.Update();

		this.configWindow = new(this);
		this.configWindow.OnSettingsUpdate += this.settingsUpdated;
		this.windowSystem = new(this.GetType().Namespace!);
		this.windowSystem.AddWindow(this.configWindow);

		Commands.AddHandler(Command, new(this.OnPluginCommand) {
			HelpMessage = $"Open {Name}'s config window",
			ShowInHelp = true,
		});

		this.dtrEntry = dtrBar.Get(Name);
		this.setDtrText();
		this.dtrEntry.Tooltip = "Click to toggle all rendering";
		this.dtrEntry.OnClick = this.dtrClickHandler;

		Interface.UiBuilder.OpenConfigUi += this.ToggleConfigUi;
		Interface.UiBuilder.Draw += this.Draw;
		this.updateCircleColours();
	}

	private void settingsUpdated() {
		this.setDtrText();
		this.updateCircleColours();
	}

	private void dtrClickHandler(DtrInteractionEvent e) {
		this.Config.Enabled = !this.Config.Enabled;
		this.setDtrText();
	}

	private void setDtrText() => this.dtrEntry.Text = $"{DTRDisplayName}: {(this.Config.Enabled ? "On" : "Off")}";

	private void updateCircleColours() {
		// fill a list containing the index and angle of all active lines, for every circle segment look which line is closest to it in terms of angleDifference
		// and use the color of that line, only needs to be done once as long as settings stay the same
		List<(int index, float angle)> lineIndexesAndAngles = [];
		for (int i = Configuration.IndexFront; i <= Configuration.IndexFrontLeft; ++i) {
			if (this.Config.DrawGuides[i])
				lineIndexesAndAngles.Add((i, this.lineIndexToAngle[i]));
		}

		if (lineIndexesAndAngles.Count == 0)
			return;

		for (int i = 0; i < this.circleSegmentIdxToAngle.Length; ++i) {
			(int index, float angle) = lineIndexesAndAngles.OrderBy(item => angleDifference(this.circleSegmentIdxToAngle[i], item.angle)).First();
			this.circleSegmentIdxToColour[i] = this.Config.LineColours[index];
		}
	}

	internal void Draw() {
		this.windowSystem.Draw();

		// If positionals matter in PVP, I won't help you. If they don't, I won't distract you.
		if (Client.IsPvP)
			return;

		if (!this.Config.Enabled)
			return;

		if (Targets.Target is not IBattleNpc target)
			return;

		if (Objects.LocalPlayer is not IPlayerCharacter player)
			return;

		if (target.ObjectKind is ObjectKind.Player) {
			if (!this.Config.DrawOnPlayers)
				return;
			if (target != Objects.LocalPlayer && this.Config.DrawOnSelfOnly)
				return;
		}

		ImGuiHelpers.ForceNextWindowMainViewport();
		ImGui.SetNextWindowPos(ImGui.GetMainViewport().Pos);
		ImGui.SetNextWindowSize(ImGui.GetMainViewport().Size);

		if (!ImGui.Begin("###PositionalGuidelineOverlay",
			ImGuiWindowFlags.NoDecoration
			| ImGuiWindowFlags.NoSavedSettings
			| ImGuiWindowFlags.NoMove
			| ImGuiWindowFlags.NoInputs
			| ImGuiWindowFlags.NoFocusOnAppearing
			| ImGuiWindowFlags.NoBackground
			| ImGuiWindowFlags.NoNav)
		) {
			return;
		}

		bool limitEither = this.Config.OnlyRenderWhenEitherEndOnScreen;
		bool limitInner = this.Config.OnlyRenderWhenCentreOnScreen;
		bool limitOuter = this.Config.OnlyRenderWhenEndpointOnScreen;

		Vector3 playerPos = player.Position;
		Vector3 targetPos = target.Position;

		// no, I don't know why it needs to be negated, but everything rotates the wrong way if it's not
		float targetFacing = -target.Rotation;

		// this is the length of the guidelines, no relation to the tether
		float length = Math.Min(this.Config.SoftMaxRange, Math.Max(this.Config.SoftMinRange, target.HitboxRadius + this.Config.SoftDrawRange));

		ImDrawListPtr drawing = ImGui.GetWindowDrawList();

		// we use `centre` later to rotate points, but if we're supposed to limit to when the TARGET is on screen and they aren't, then we stop early
		bool targetOnScreen = Gui.WorldToScreen(targetPos, out Vector2 centre);
		if (!targetOnScreen && !limitEither && limitInner)
			return;

		// +X = east, -X = west
		// +Z = south, -Z = north
		Vector3 guidelineBasePoint2 = targetPos + new Vector3(0, 0, length);
		Vector3 circleBasePoint = targetPos + new Vector3(0, 0, target.HitboxRadius);
		bool anyLineActive = false;
		for (int lineIndex = Configuration.IndexFront; lineIndex <= Configuration.IndexFrontLeft; ++lineIndex) {
			if (!this.Config.DrawGuides[lineIndex])
				continue;
			anyLineActive = true;

			Vector3 rotated = rotatePoint(targetPos, guidelineBasePoint2, targetFacing + this.lineIndexToAngle[lineIndex]);
			bool endpointOnScreen = Gui.WorldToScreen(rotated, out Vector2 coord);
			if (limitEither) {
				if (!targetOnScreen && !endpointOnScreen)
					continue;
			}
			else if (limitOuter && !endpointOnScreen) {
				continue;
			}
			drawing.AddLine(centre, coord, ImGui.GetColorU32(this.Config.LineColours[lineIndex]), this.Config.LineThickness);
		}

		if (this.Config.DrawCircle)
			this.drawCircle(drawing, targetPos, circleBasePoint, targetFacing, anyLineActive, CircleTypes.Target);

		if (this.Config.DrawOuterCircle)
			this.drawCircle(drawing, targetPos, circleBasePoint, targetFacing, anyLineActive, CircleTypes.Outer);

		// the tether line itself used to be pretty simple: from our position to the target's
		// then I decided to allow controlling the maximum length of it and suddenly everything got Complicated
		if (this.Config.DrawTetherLine && target != player) {

			// radians between DUE SOUTH (angle=0) and PLAYER POSITION using TARGET POSITION as the vertex
			double angleToPlayer = angleBetween(targetPos, targetPos + Vector3.UnitZ, playerPos);

			// radians between DUE SOUTH (angle=0) and TARGET POSITION using PLAYER POSITION as the vertex
			double angleToTarget = angleBetween(playerPos, playerPos + Vector3.UnitZ, targetPos);

			// the scalar offset from the target position towards their target ring, in the direction DUE SOUTH (angle=0)
			// if set distance is -1, point is the centre of the target, so offset is 0
			float offset = this.Config.TetherLengthInner == -1
				? 0
				: Math.Max(target.HitboxRadius - this.Config.SoftInnerTetherLength, 0);

			// the world position for the tether's inner point, as the offset point rotated around the centre to face the player
			Vector3 tetherInner = offset == 0
				? targetPos
				: rotatePoint(targetPos, targetPos + new Vector3(0, 0, offset), angleToPlayer);

			// the world position for the tether's outer point, extending the set distance from the target's ring in the direction of the player
			// or, if set distance is -1, to the player's centre; if -2, to the edge of the player's ring
			Vector3 tetherOuter = this.Config.TetherLengthOuter switch {
				-2 => rotatePoint(playerPos, playerPos + new Vector3(0, 0, player.HitboxRadius), angleToTarget),
				-1 => playerPos,
				_ => rotatePoint(targetPos, targetPos + new Vector3(0, 0, target.HitboxRadius + this.Config.SoftOuterTetherLength), angleToPlayer).WithY(playerPos.Y),
			};

			if (this.Config.FlattenTether)
				tetherOuter.Y = tetherInner.Y;

			// check whether we're allowed to render, in case points are off screen
			bool insideOnScreen = Gui.WorldToScreen(tetherInner, out Vector2 inner);
			bool outsideOnScreen = Gui.WorldToScreen(tetherOuter, out Vector2 outer);
			bool draw = true;
			if (limitEither) {
				if (!insideOnScreen && !outsideOnScreen)
					draw = false;
			}
			else {
				if (limitInner && !insideOnScreen)
					draw = false;
				if (limitOuter && !outsideOnScreen)
					draw = false;
			}

			// and finally, draw the line (but only if we're allowed to)
			if (draw)
				drawing.AddLine(inner, outer, ImGui.GetColorU32(this.Config.TetherColour), this.Config.LineThickness);
		}

		ImGui.End();
	}

	private void drawCircle(ImDrawListPtr drawing, Vector3 targetPos, Vector3 basePoint, float targetFacing, bool anyLineActive, CircleTypes circleType) {
		Vector4 circleColour = new(1, 0, 0, 1);
		Vector3 circleBasePoint = basePoint;
		bool forceCircleColour = false;

		switch (circleType) {
			case CircleTypes.Target:
				circleColour = this.Config.LineColours[Configuration.IndexCircle];
				forceCircleColour = this.Config.AlwaysUseCircleColours || this.Config.AlwaysUseCircleColoursTarget || !anyLineActive;
				break;
			case CircleTypes.Outer:
				circleColour = this.Config.LineColours[Configuration.IndexOuterCircle];
				forceCircleColour = this.Config.AlwaysUseCircleColours || this.Config.AlwaysUseCircleColoursOuter || !anyLineActive;
				circleBasePoint += new Vector3(0, 0, this.Config.SoftOuterCircleRange);
				break;
		}

		Vector3 startPoint = rotatePoint(targetPos, circleBasePoint, targetFacing);
		Vector3[] points = circlePoints(targetPos, startPoint, this.circleSegmentIdxToAngle).ToArray();

		(Vector2 point, bool render)[] screenPoints = new (Vector2 point, bool render)[points.Length];
		for (int i = 0; i < points.Length; ++i) {
			bool render = Gui.WorldToScreen(points[i], out Vector2 screenPoint);
			screenPoints[i] = (screenPoint, render);
		}

		for (int i = 0; i < screenPoints.Length; ++i) {
			int nextIndex = (i + 1) % screenPoints.Length;
#pragma warning disable IDE0042 // Deconstruct variable declaration
			(Vector2 point, bool render) curPoint = screenPoints[i];
			(Vector2 point, bool render) nextPoint = screenPoints[nextIndex];
#pragma warning restore IDE0042 // Deconstruct variable declaration

			if (curPoint.render && nextPoint.render) {
				Vector4 colour = forceCircleColour ? circleColour : this.circleSegmentIdxToColour[i];
				drawing.AddLine(curPoint.point, nextPoint.point, ImGui.GetColorU32(colour), this.Config.LineThickness + 2);
			}
		}
	}

	private static Vector2 rotatePoint(Vector2 centre, Vector2 originalPoint, double angleRadians) {
		// Adapted (read: shamelessly stolen) from https://github.com/PunishedPineapple/Distance

		Vector2 translatedOriginPoint = originalPoint - centre;
		float distance = new Vector2(translatedOriginPoint.X, translatedOriginPoint.Y).Length();
		double translatedAngle = Math.Atan2(translatedOriginPoint.Y, translatedOriginPoint.X);

		return new(
			((float)Math.Cos(translatedAngle + angleRadians) * distance) + centre.X,
			((float)Math.Sin(translatedAngle + angleRadians) * distance) + centre.Y
		);
	}
	private static Vector3 rotatePoint(Vector3 centre, Vector3 originalPoint, double angleRadians) {
		Vector2 rotated = rotatePoint(new Vector2(centre.X, centre.Z), new Vector2(originalPoint.X, originalPoint.Z), angleRadians);
		return new(rotated.X, centre.Y, rotated.Y);
	}

	private static double angleBetween(Vector2 vertex, Vector2 a, Vector2 b) => Math.Atan2(b.Y - vertex.Y, b.X - vertex.X) - Math.Atan2(a.Y - vertex.Y, a.X - vertex.X);
	private static double angleBetween(Vector3 vertex, Vector3 a, Vector3 b) => angleBetween(new Vector2(vertex.X, vertex.Z), new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));
	private static float angleDifference(float a, float b) => (float)Math.Min(Math.Abs(a - b), Math.Abs(Math.Abs(a - b) - (2 * Math.PI)));

	private static IEnumerable<Vector2> circlePoints(Vector2 centre, Vector2 start, float[] angles) {
		foreach (float angle in angles)
			yield return rotatePoint(centre, start, angle);
	}
	private static IEnumerable<Vector3> circlePoints(Vector3 centre, Vector3 start, float[] angles)
		=> circlePoints(new Vector2(centre.X, centre.Z), new Vector2(start.X, start.Z), angles).Select(v2 => new Vector3(v2.X, centre.Y, v2.Y));

	internal void OnPluginCommand(string command, string arguments) {
		string[] args = arguments.Trim().Split();

		string action = args.Length >= 1 ? args[0].ToLower() : "config";
		string target = args.Length >= 2 ? args[1].ToLower() : "render";
		if (string.IsNullOrEmpty(action))
			action = "config";

		bool? state;

		switch (action) {
			case "enable":
				state = true;
				break;
			case "disable":
				state = false;
				break;
			case "toggle":
				state = null;
				break;
			case "config":
				this.ToggleConfigUi();
				return;
			case "swap":
				target = "all";
				state = null;
				break;
			default:
				Chat.PrintError($"Unknown action '{args[0]}'");
				return;
		}

		switch (target) {
			case "f":
			case "front":
				this.Config.DrawFront = state ?? !this.Config.DrawFront;
				break;
			case "fr":
			case "frontright":
			case "front-right":
				this.Config.DrawFrontRight = state ?? !this.Config.DrawFrontRight;
				break;
			case "r":
			case "right":
				this.Config.DrawRight = state ?? !this.Config.DrawRight;
				break;
			case "br":
			case "backright":
			case "back-right":
				this.Config.DrawBackRight = state ?? !this.Config.DrawBackRight;
				break;
			case "b":
			case "back":
				this.Config.DrawBack = state ?? !this.Config.DrawBack;
				break;
			case "bl":
			case "backleft":
			case "back-left":
				this.Config.DrawBackLeft = state ?? !this.Config.DrawBackLeft;
				break;
			case "l":
			case "left":
				this.Config.DrawLeft = state ?? !this.Config.DrawLeft;
				break;
			case "fl":
			case "frontleft":
			case "front-left":
				this.Config.DrawFrontLeft = state ?? !this.Config.DrawFrontLeft;
				break;
			case "cardinal":
			case "cardinals":
				this.Config.DrawFront = state ?? !this.Config.DrawFront;
				this.Config.DrawRight = state ?? !this.Config.DrawRight;
				this.Config.DrawBack = state ?? !this.Config.DrawBack;
				this.Config.DrawLeft = state ?? !this.Config.DrawLeft;
				break;
			case "diagonal":
			case "diagonals":
				this.Config.DrawFrontRight = state ?? !this.Config.DrawFrontRight;
				this.Config.DrawBackRight = state ?? !this.Config.DrawBackRight;
				this.Config.DrawBackLeft = state ?? !this.Config.DrawBackLeft;
				this.Config.DrawFrontLeft = state ?? !this.Config.DrawFrontLeft;
				break;
			case "lines":
				this.Config.DrawFront = state ?? !this.Config.DrawFront;
				this.Config.DrawRight = state ?? !this.Config.DrawRight;
				this.Config.DrawBack = state ?? !this.Config.DrawBack;
				this.Config.DrawLeft = state ?? !this.Config.DrawLeft;
				this.Config.DrawFrontRight = state ?? !this.Config.DrawFrontRight;
				this.Config.DrawBackRight = state ?? !this.Config.DrawBackRight;
				this.Config.DrawBackLeft = state ?? !this.Config.DrawBackLeft;
				this.Config.DrawFrontLeft = state ?? !this.Config.DrawFrontLeft;
				break;
			case "c":
			case "circle":
			case "target-circle":
				this.Config.DrawCircle = state ?? !this.Config.DrawCircle;
				break;
			case "co":
			case "outer-circle":
			case "outer":
				this.Config.DrawOuterCircle = state ?? !this.Config.DrawOuterCircle;
				break;
			case "circles":
				this.Config.DrawCircle = state ?? !this.Config.DrawCircle;
				this.Config.DrawOuterCircle = state ?? !this.Config.DrawOuterCircle;
				break;
			case "all":
				this.Config.DrawFront = state ?? !this.Config.DrawFront;
				this.Config.DrawRight = state ?? !this.Config.DrawRight;
				this.Config.DrawBack = state ?? !this.Config.DrawBack;
				this.Config.DrawLeft = state ?? !this.Config.DrawLeft;
				this.Config.DrawFrontRight = state ?? !this.Config.DrawFrontRight;
				this.Config.DrawBackRight = state ?? !this.Config.DrawBackRight;
				this.Config.DrawBackLeft = state ?? !this.Config.DrawBackLeft;
				this.Config.DrawFrontLeft = state ?? !this.Config.DrawFrontLeft;
				this.Config.DrawCircle = state ?? !this.Config.DrawCircle;
				this.Config.DrawOuterCircle = state ?? !this.Config.DrawOuterCircle;
				break;
			case "tether":
				this.Config.DrawTetherLine = state ?? !this.Config.DrawTetherLine;
				//Notifications.AddNotification(new() {
				//	Title = Name,
				//	Content = $"Tether rendering {(this.Config.DrawTetherLine ? "enabled" : "disabled")}",
				//	Type = NotificationType.Info,
				//	Minimized = true,
				//	MinimizedText = $"Tethers {(this.Config.DrawTetherLine ? "on" : "off")}",
				//});
				break;
			case "render":
				this.Config.Enabled = state ?? !this.Config.Enabled;
				//Notifications.AddNotification(new() {
				//	Title = Name,
				//	Content = $"Guide rendering {(this.Config.DrawTetherLine ? "enabled" : "disabled")}",
				//	Type = NotificationType.Info,
				//	Minimized = true,
				//	MinimizedText = $"Guides {(this.Config.DrawTetherLine ? "on" : "off")}",
				//});
				break;
			default:
				Chat.PrintError($"Unknown target '{args[1]}'");
				return;
		}

		this.settingsUpdated();
		Interface.SavePluginConfig(this.Config);
	}

	internal void ToggleConfigUi() {
		if (this.configWindow is not null) {
			this.configWindow.IsOpen = !this.configWindow.IsOpen;
		}
		else {
			Log.Error("Cannot toggle configuration window, reference does not exist");
		}
	}

	#region Disposable
	protected virtual void Dispose(bool disposing) {
		if (this.disposed)
			return;
		this.disposed = true;

		if (disposing) {
			Interface.UiBuilder.OpenConfigUi -= this.ToggleConfigUi;
			Interface.UiBuilder.Draw -= this.Draw;
			this.configWindow.OnSettingsUpdate -= this.settingsUpdated;

			Commands.RemoveHandler(Command);

			this.configWindow.Dispose();
			this.dtrEntry.Remove();
		}
	}

	public void Dispose() {
		this.Dispose(true);
		GC.SuppressFinalize(this);
	}
	#endregion
}

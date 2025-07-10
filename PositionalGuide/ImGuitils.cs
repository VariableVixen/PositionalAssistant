using Dalamud.Interface.Utility;

using ImGuiNET;

namespace VariableVixen.PositionalGuide;

internal class ImGuitils {
	public int TooltipPixelWrapWidth;

	public ImGuitils(int tooltipPixelWrapWidth) => this.TooltipPixelWrapWidth = tooltipPixelWrapWidth;

	public void Tooltip(string text) {
		if (ImGui.IsItemHovered()) {
			ImGui.BeginTooltip();
			ImGui.PushTextWrapPos(ImGuiHelpers.GlobalScale * this.TooltipPixelWrapWidth);
			ImGui.TextUnformatted(text);
			ImGui.PopTextWrapPos();
			ImGui.EndTooltip();
		}
	}
}

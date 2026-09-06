using Dalamud.Game.ClientState.JobGauge.Types;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Statuses;

namespace VariableVixen.PositionalGuide;

/// <summary>
/// Which side of the target the player's next positional weaponskill wants to be hit from.
/// </summary>
internal enum RequiredPositional {
	None,
	Rear,
	Flank,
}

/// <summary>
/// Works out, from live job state, which positional the player's next weaponskill is going to want.
/// </summary>
internal static class Positionals {
	private const uint JobMonk = 20;

	private const uint
		StatusCoeurlForm = 109,
		StatusPerfectBalance = 110;

	internal static RequiredPositional Detect(IPlayerCharacter player, Configuration config)
		=> player.ClassJob.RowId is JobMonk ? detectMonk(player, config) : RequiredPositional.None;

	private static RequiredPositional detectMonk(IPlayerCharacter player, Configuration config) {
		// As of Dawntrail, coeurl form holds monk's only two positionals: Demolish wants the rear,
		// Snap Punch / Pouncing Coeurl wants a flank. Nothing in opo-opo or raptor form has one at all.
		bool coeurlForm = false;
		bool perfectBalance = false;
		foreach (IStatus status in player.StatusList) {
			if (status is null)
				continue;
			if (status.StatusId is StatusCoeurlForm)
				coeurlForm = true;
			else if (status.StatusId is StatusPerfectBalance)
				perfectBalance = true;
		}

		// Perfect Balance suppresses forms and lets any of them be used. The coeurl-chakra filler is
		// Demolish (Pouncing Coeurl would spend a Fury stack for the same chakra), so the rear is the
		// only side that can be wrong to stand on during the window.
		if (perfectBalance)
			return config.HighlightDuringPerfectBalance ? RequiredPositional.Rear : RequiredPositional.None;

		if (!coeurlForm)
			return RequiredPositional.None;

		// Demolish grants Coeurl's Fury, Pouncing Coeurl spends it, so a stack on the gauge means the
		// next coeurl-form button is the flank one.
		return Plugin.Gauges.Get<MNKGauge>().CoeurlFury > 0
			? RequiredPositional.Flank
			: RequiredPositional.Rear;
	}
}

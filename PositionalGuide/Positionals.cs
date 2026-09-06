using Dalamud.Game.ClientState.JobGauge.Types;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Statuses;

using FFXIVClientStructs.FFXIV.Client.Game;

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
/// Every job here returns <see cref="RequiredPositional.None"/> unless the next weaponskill genuinely
/// has a positional, so the guides fall back to their configured colours the rest of the time.
/// </summary>
internal static class Positionals {
	private const uint
		JobDragoon = 22,
		JobMonk = 20,
		JobNinja = 30,
		JobReaper = 39,
		JobSamurai = 34,
		JobViper = 41;

	// monk
	private const uint
		StatusCoeurlForm = 109,
		StatusPerfectBalance = 110;

	// dragoon - the pre-upgrade actions are listed too so the guides still work below max level
	private const uint
		ActionDisembowel = 87,
		ActionSpiralBlow = 36903,
		ActionChaosThrust = 88,
		ActionChaoticSpring = 25772,
		ActionFullThrust = 84,
		ActionHeavensThrust = 25771;

	// ninja
	private const uint ActionGustSlash = 2242;

	/// <summary>
	/// Armor Crush grants two Kazematoi against a cap of five, so it goes out while three or fewer are
	/// banked and Aeolian Edge spends them the rest of the time.
	/// </summary>
	private const byte KazematoiSpendThreshold = 4;

	// samurai
	private const uint
		ActionJinpu = 7478,
		ActionShifu = 7479;

	// reaper
	private const uint
		StatusSoulReaver = 2587,
		StatusEnhancedGibbet = 2588,
		StatusEnhancedGallows = 2589,
		StatusExecutioner = 3858;

	// viper
	private const uint
		ActionHuntersSting = 34608,
		ActionSwiftskinsSting = 34609;

	internal static RequiredPositional Detect(IPlayerCharacter player, Configuration config) => player.ClassJob.RowId switch {
		JobDragoon => detectDragoon(),
		JobMonk => detectMonk(player, config),
		JobNinja => detectNinja(),
		JobReaper => detectReaper(player),
		JobSamurai => detectSamurai(),
		JobViper => detectViper(),
		_ => RequiredPositional.None,
	};

	/// <summary>
	/// The action that most recently advanced the player's combo, or zero if the combo has lapsed.
	/// </summary>
	private static unsafe uint comboAction() {
		ActionManager* manager = ActionManager.Instance();
		if (manager is null)
			return 0;

		return manager->Combo.Timer > 0 ? manager->Combo.Action : 0;
	}

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

	/// <summary>
	/// Dragoon's positionals sit at fixed steps of its two combo branches, so the combo alone settles it.
	/// </summary>
	private static RequiredPositional detectDragoon() => comboAction() switch {
		ActionDisembowel or ActionSpiralBlow => RequiredPositional.Rear, // into Chaotic Spring
		ActionChaosThrust or ActionChaoticSpring => RequiredPositional.Rear, // into Wheeling Thrust
		ActionFullThrust or ActionHeavensThrust => RequiredPositional.Flank, // into Fang and Claw
		_ => RequiredPositional.None,
	};

	/// <summary>
	/// Aeolian Edge (rear) and Armor Crush (flank) are the same step of the same combo, picked apart by
	/// how much Kazematoi is banked.
	/// </summary>
	private static RequiredPositional detectNinja() {
		if (comboAction() is not ActionGustSlash)
			return RequiredPositional.None;

		return Plugin.Gauges.Get<NINGauge>().Kazematoi >= KazematoiSpendThreshold
			? RequiredPositional.Rear
			: RequiredPositional.Flank;
	}

	/// <summary>
	/// Gibbet (flank) and Gallows (rear) alternate: each one grants the buff that makes the other the
	/// stronger follow-up. With neither buff up they hit for the same, so there is nothing to advise.
	/// </summary>
	private static RequiredPositional detectReaper(IPlayerCharacter player) {
		bool reaverWindow = false;
		bool enhancedGibbet = false;
		bool enhancedGallows = false;
		foreach (IStatus status in player.StatusList) {
			if (status is null)
				continue;
			switch (status.StatusId) {
				case StatusSoulReaver or StatusExecutioner:
					reaverWindow = true;
					break;
				case StatusEnhancedGibbet:
					enhancedGibbet = true;
					break;
				case StatusEnhancedGallows:
					enhancedGallows = true;
					break;
			}
		}

		if (!reaverWindow)
			return RequiredPositional.None;
		if (enhancedGallows)
			return RequiredPositional.Rear;
		if (enhancedGibbet)
			return RequiredPositional.Flank;

		return RequiredPositional.None;
	}

	/// <summary>
	/// Gekko follows Jinpu and wants the rear, Kasha follows Shifu and wants a flank.
	/// </summary>
	private static RequiredPositional detectSamurai() => comboAction() switch {
		ActionJinpu => RequiredPositional.Rear,
		ActionShifu => RequiredPositional.Flank,
		_ => RequiredPositional.None,
	};

	/// <summary>
	/// Both finishers on each viper branch share a side, so the branch is all that matters: Hunter's Sting
	/// leads into the flank pair, Swiftskin's Sting into the rear pair.
	/// </summary>
	private static RequiredPositional detectViper() => comboAction() switch {
		ActionHuntersSting => RequiredPositional.Flank,
		ActionSwiftskinsSting => RequiredPositional.Rear,
		_ => RequiredPositional.None,
	};
}

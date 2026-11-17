using System;
using System.Collections.Generic;
using System.Linq;

namespace FlipPix.Core.Models;

/// <summary>The five action LoRAs the 🥋 Action tab mixes, in the order they are stacked.</summary>
public enum H3CombatLora { Combat, Weapon, GunFu, Motion, FightFlow }

/// <summary>One strength per <see cref="H3CombatLora"/>.</summary>
public readonly record struct H3CombatMix(double Combat, double Weapon, double GunFu, double Motion, double FightFlow)
{
    public double this[H3CombatLora lora] => lora switch
    {
        H3CombatLora.Combat => Combat,
        H3CombatLora.Weapon => Weapon,
        H3CombatLora.GunFu => GunFu,
        H3CombatLora.Motion => Motion,
        _ => FightFlow,
    };

    public bool IsEmpty => Combat <= 0 && Weapon <= 0 && GunFu <= 0 && Motion <= 0 && FightFlow <= 0;

    /// <summary>"Combat 0.65 · Fight Flow 0.65", the LoRAs above zero only.</summary>
    public string Describe()
    {
        var mix = this;
        var parts = H3CombatPreset.Loras.Where(l => mix[l.Lora] > 0).Select(l => $"{l.Label} {mix[l.Lora]:0.##}").ToList();
        return parts.Count == 0 ? "no action LoRA" : string.Join(" · ", parts);
    }
}

/// <summary>A LoRA of the mix: its file, where to get it, and what it is for.</summary>
public sealed record H3CombatLoraFile(H3CombatLora Lora, string Label, string FileName, string Url, string Purpose);

/// <summary>
/// The nine combat presets of "MiniMax H3 Action Director" (01_minimax-h3-action-director-9-combat-presets.json,
/// JOKER141's LoRAs). The workflow picks a preset by number and computes each LoRA's strength with a
/// ComfyMathExpression per LoRA — one for the first pass, one for the low-noise refine — and this table is those
/// expressions evaluated at 0…8. Nothing here is tuned by FlipPix.
///
/// <para>The refine mix is deliberately weaker: at full strength on a 0.3–0.4 denoise the motion LoRAs redraw
/// limbs the first pass already placed.</para>
/// </summary>
public sealed record H3CombatPreset(
    int Number, string Code, string Name, string Summary, H3CombatMix FirstPass, H3CombatMix Refine,
    string Lead, string Rules, string Brief)
{
    public const int DefaultNumber = 2;

    /// <summary>"A2 · Two-person attack and defence" — what the dropdown shows.</summary>
    public string Label => $"{Code} · {Name}";

    public static IReadOnlyList<H3CombatLoraFile> Loras { get; } = new[]
    {
        new H3CombatLoraFile(H3CombatLora.Combat, "Combat", "H3_Combat_V2.safetensors",
            "https://huggingface.co/JOKER141/MiniMax-H3-Combat-Base-V2/resolve/main/H3_Combat_V2.safetensors",
            "Unarmed strikes, blocks and body contact."),
        new H3CombatLoraFile(H3CombatLora.Weapon, "Weapon", "Bunny_weapon_combatV1.safetensors",
            "https://huggingface.co/JOKER141/MiniMax-H3-Weapon-Combat-LoRA/resolve/main/Bunny_weapon_combatV1.safetensors",
            "Swords, staffs and other hand weapons."),
        new H3CombatLoraFile(H3CombatLora.GunFu, "GunFu", "GunFu.safetensors",
            "https://huggingface.co/JOKER141/MiniMaxH3-GunFu-Gunfight-LORA/resolve/main/GunFu.safetensors",
            "Firearms handled at speed and at close range."),
        new H3CombatLoraFile(H3CombatLora.Motion, "Motion Repair", "Motion_Repair_V2.safetensors",
            "https://huggingface.co/JOKER141/MiniMax-H3-General-Motion-Continuity-Repair/resolve/main/Motion_Repair_V2.safetensors",
            "Keeps fast limbs whole and motion continuous."),
        new H3CombatLoraFile(H3CombatLora.FightFlow, "Fight Flow", "H3_Fight_Flow_V1.safetensors",
            "https://huggingface.co/JOKER141/H3-Fight-Flow-Fight-Interaction-LoRA/resolve/main/H3_Fight_Flow_V1.safetensors",
            "Two fighters reacting to each other, exchange after exchange."),
    };

    private const string UnarmedRules =
        "Every strike has a target and ends in visible contact, a block or an evasion; bodies react to each hit with weight and inertia. " +
        "No weapons, no slow motion, no energy effects.";

    public static IReadOnlyList<H3CombatPreset> All { get; } = new[]
    {
        new H3CombatPreset(0, "A0", "Basic strikes", "Punches and kicks, one clean exchange at a time.",
            new(0.65, 0, 0, 0, 0), new(0.40, 0, 0, 0, 0),
            "Unarmed combat.", UnarmedRules,
            "simple, readable punches, kicks and blocks — one technique at a time, each fully finished before the next"),
        new H3CombatPreset(1, "A1", "High-motion repair", "Fast, wide movement — spins, jumps, falls — kept whole.",
            new(1.00, 0, 0, 0.50, 0), new(0.65, 0, 0, 0.20, 0),
            "High-speed unarmed combat.", UnarmedRules,
            "fast, large movements — spinning kicks, leaps, rolls, throws and falls — with every limb accounted for"),
        new H3CombatPreset(2, "A2", "Two-person attack and defence", "The default: attack, block, counter between two fighters.",
            new(0.65, 0, 0, 0, 0.65), new(0.40, 0, 0, 0, 0.25),
            "Unarmed combat interaction.", UnarmedRules,
            "a two-person exchange — one attacks, the other blocks or evades and counters straight away, initiative passing back and forth"),
        new H3CombatPreset(3, "A3", "Mixed martial arts", "Strikes, clinch, throws and scrambles in one flow.",
            new(0.70, 0, 0, 0.50, 0.65), new(0.45, 0, 0, 0.20, 0.25),
            "Unarmed combat interaction, mixed martial arts.", UnarmedRules,
            "mixed fighting — striking into clinch work, knees and elbows, throws, takedowns and scrambles back to the feet"),
        new H3CombatPreset(4, "A4", "Blades and weapons", "Swords, knives, staffs: cuts, parries and binds.",
            new(0, 0.45, 0, 0, 0), new(0, 0.25, 0, 0, 0),
            "Weapon combat.",
            "Each weapon stays in the same hand and keeps its length and shape; every cut or thrust is parried, bound, dodged or lands with a visible reaction. " +
            "No slow motion, no energy effects, no gore.",
            "armed fighting with the weapons in the picture — cuts, thrusts, parries, binds and footwork; name which hand holds what"),
        new H3CombatPreset(5, "A5", "Gun-fu", "Stylised gunplay: draw, aim, fire, move.",
            new(0, 0, 0.90, 0, 0), new(0, 0, 0.65, 0, 0),
            "Gun-fu action.",
            "Each firearm stays in the same hand; every shot shows a muzzle flash, recoil and a target that reacts. No slow motion, no gore.",
            "stylised gunplay — drawing, aiming, firing on the move, reloads and cover, each shot with recoil and a consequence"),
        new H3CombatPreset(6, "A6", "Sustained gunfight", "A running firefight held together over many shots.",
            new(0, 0, 0.90, 0.50, 0), new(0, 0, 0.65, 0.25, 0),
            "Continuous gunfight.",
            "Each firearm stays in the same hand; every burst shows muzzle flash, recoil and impacts on the surroundings. No slow motion, no gore.",
            "a sustained firefight — advancing between cover, bursts of fire, return fire striking the surroundings, reloads under pressure"),
        new H3CombatPreset(7, "A7", "Close-quarters gun-fu", "Guns inside arm's reach: parry the barrel, strike, fire.",
            new(0.50, 0, 0.90, 0.30, 0), new(0, 0, 0.65, 0.25, 0),
            "Close-quarters gun-fu, gun and hand-to-hand combat.",
            "Each firearm stays in the same hand; barrels are parried and redirected, strikes land with weight, shots show flash and recoil. No slow motion, no gore.",
            "gunplay inside arm's reach — barrels slapped aside, wrist controls, elbows and knees between shots, disarms"),
        new H3CombatPreset(8, "A8", "General motion repair", "Any action — chases, stunts, sport — with no combat styling.",
            new(0, 0, 0, 0.90, 0), new(0, 0, 0, 0.30, 0),
            "Dynamic action.",
            "Movement is continuous and physically plausible, limbs stay whole, momentum carries through. No slow motion.",
            "non-combat action — running, climbing, vaulting, falling, stunts or sport — as one continuous physical movement"),
    };

    public static H3CombatPreset ByNumber(int number) =>
        All.FirstOrDefault(p => p.Number == number) ?? All[DefaultNumber];

    /// <summary>
    /// The refine mix for a hand-set first pass — the workflow's ⑦-on, ⑧-off branch: each strength scaled
    /// down and capped (<c>min(cap, c × factor)</c>).
    /// </summary>
    public static H3CombatMix RefineFor(H3CombatMix manual) => new(
        Math.Round(Math.Min(0.65, manual.Combat * 0.65), 4),
        Math.Round(Math.Min(0.35, manual.Weapon * 0.55), 4),
        Math.Round(Math.Min(0.65, manual.GunFu * 0.7222222222), 4),
        Math.Round(Math.Min(0.30, manual.Motion * 0.4), 4),
        Math.Round(Math.Min(0.25, manual.FightFlow * 0.3846153846), 4));
}

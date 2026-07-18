import { create } from "zustand";
import type { FarmingStrategy } from "../api/strategy";
import type { Settings } from "../api/types";

/**
 * Setup-wizard draft. Nothing is written to the bot until the Review step: build-scope settings
 * accumulate in `settingsDraft` (applied as a PATCH), and the farming strategy accumulates in
 * `strategyDraft` (created/saved + activated). Autosaved to localStorage so a refresh mid-setup
 * doesn't lose progress.
 */
export interface WizardState {
  step: number;
  /** Full settings document being edited (build scope: skills, flasks, combat, movement). */
  settingsDraft: Settings | null;
  /** Settings version captured at load, for the PATCH concurrency check. */
  settingsVersion: number;
  /** The strategy being built from a template. Null until an archetype is chosen. */
  strategyDraft: FarmingStrategy | null;
  /** True when the chosen archetype is a legacy settings-configured mode (Blight/Simulacrum). */
  legacyMode: number | null;
}

const STORAGE_KEY = "bubblesbot.wizard.draft";

function load(): Partial<WizardState> {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as Partial<WizardState>) : {};
  } catch {
    return {};
  }
}

export const useWizardStore = create<WizardState>(() => ({
  step: 0,
  settingsDraft: null,
  settingsVersion: 0,
  strategyDraft: null,
  legacyMode: null,
  ...load(),
}));

useWizardStore.subscribe((state) => {
  try {
    // Persist only the serializable draft (not transient step chrome would also be fine).
    localStorage.setItem(STORAGE_KEY, JSON.stringify({
      step: state.step,
      settingsDraft: state.settingsDraft,
      settingsVersion: state.settingsVersion,
      strategyDraft: state.strategyDraft,
      legacyMode: state.legacyMode,
    }));
  } catch { /* storage full / disabled — draft just won't persist */ }
});

export function clearWizardDraft(): void {
  try { localStorage.removeItem(STORAGE_KEY); } catch { /* ignore */ }
  useWizardStore.setState({ step: 0, settingsDraft: null, settingsVersion: 0, strategyDraft: null, legacyMode: null });
}

import { useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  ApiError, controlArm, controlMode, fetchMeta, fetchSchema, fetchSettings, patchSettings,
  type BotMeta, type FieldError, type PatchOp,
} from "../api/client";
import type { Schema, Settings } from "../api/types";
import {
  activateStrategy, createStrategy, listTemplates, saveStrategy,
} from "../api/strategyClient";
import type { FarmingStrategy, StrategyTemplate } from "../api/strategy";
import { fieldPath, pathGet, pathSet, sameValue } from "../lib/paths";
import { SchemaForm } from "../components/schema/SchemaForm";
import { MechanicsTab } from "../components/strategy/MechanicsTab";
import { SuppliesTab } from "../components/strategy/SuppliesTab";
import { GeneralTab } from "../components/strategy/GeneralTab";
import { LimitsTab } from "../components/strategy/LimitsTab";
import { useStatusStore } from "../state/statusStore";
import { clearWizardDraft, useWizardStore } from "../state/wizardStore";

type StepKind = "preflight" | "character" | "skills" | "flasksMovement" | "archetype"
  | "mechanics" | "supplies" | "limits" | "review" | "arm";

interface StepDef { kind: StepKind; title: string; strategyOnly?: boolean }

const STEPS: StepDef[] = [
  { kind: "preflight", title: "Preflight" },
  { kind: "character", title: "Character" },
  { kind: "skills", title: "Skills" },
  { kind: "flasksMovement", title: "Flasks & movement" },
  { kind: "archetype", title: "Strategy" },
  { kind: "mechanics", title: "Mechanics", strategyOnly: true },
  { kind: "supplies", title: "Supplies", strategyOnly: true },
  { kind: "limits", title: "Limits", strategyOnly: true },
  { kind: "review", title: "Review" },
  { kind: "arm", title: "Arm" },
];

export default function WizardPage() {
  const navigate = useNavigate();
  const store = useWizardStore();
  const [schema, setSchema] = useState<Schema | null>(null);
  const [original, setOriginal] = useState<Settings | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (store.settingsDraft) return;   // resuming a saved draft
    Promise.all([fetchSchema(), fetchSettings()])
      .then(([sc, envelope]) => {
        setSchema(sc);
        setOriginal(envelope.settings);
        useWizardStore.setState({ settingsDraft: envelope.settings, settingsVersion: envelope.version });
      })
      .catch((e) => setError(String(e)));
  }, [store.settingsDraft]);

  // Load the schema even when resuming a draft (schema isn't persisted).
  useEffect(() => {
    if (!schema) fetchSchema().then(setSchema).catch((e) => setError(String(e)));
    if (!original) fetchSettings().then((e) => setOriginal(e.settings)).catch(() => {});
  }, [schema, original]);

  // Steps that apply: strategy-only steps are skipped when a legacy (Blight/Simulacrum) mode was chosen.
  const steps = useMemo(
    () => STEPS.filter((s) => !s.strategyOnly || store.strategyDraft !== null),
    [store.strategyDraft],
  );
  const stepIndex = Math.min(store.step, steps.length - 1);
  const step = steps[stepIndex];

  const setSettings = (path: string[], value: unknown) =>
    useWizardStore.setState((s) => ({ settingsDraft: s.settingsDraft ? pathSet(s.settingsDraft, path, value) : s.settingsDraft }));
  const setStrategy = (next: FarmingStrategy) => useWizardStore.setState({ strategyDraft: next });

  const goto = (i: number) => useWizardStore.setState({ step: Math.max(0, Math.min(i, steps.length - 1)) });

  if (error) return <section className="card"><h2>Setup</h2><div className="v bad">{error}</div></section>;
  if (!schema || !store.settingsDraft) return <section className="card"><h2>Setup</h2><div className="tree-empty">loading…</div></section>;

  return (
    <>
      <section className="card">
        <div className="wizard-steps">
          {steps.map((s, i) => (
            <span key={s.kind} className={`wizard-step ${i === stepIndex ? "active" : ""} ${i < stepIndex ? "done" : ""}`}>
              {i + 1}. {s.title}
            </span>
          ))}
        </div>
      </section>

      <section className="card">
        <h2>{step.title}</h2>
        {step.kind === "preflight" && <PreflightStep />}
        {step.kind === "character" && <CharacterStep />}
        {step.kind === "skills" && (
          <SchemaForm fields={schema.fields} values={store.settingsDraft} saved={original ?? store.settingsDraft}
            onChange={setSettings} categories={["Skills"]} />
        )}
        {step.kind === "flasksMovement" && (
          <SchemaForm fields={schema.fields} values={store.settingsDraft} saved={original ?? store.settingsDraft}
            onChange={setSettings} categories={["Flasks", "Combat", "Movement", "Exploration"]} />
        )}
        {step.kind === "archetype" && <ArchetypeStep onError={setError} />}
        {step.kind === "mechanics" && store.strategyDraft && (
          <MechanicsTab strategy={store.strategyDraft} onChange={setStrategy} />
        )}
        {step.kind === "supplies" && store.strategyDraft && (
          <SuppliesTab strategy={store.strategyDraft} onChange={setStrategy} />
        )}
        {step.kind === "limits" && store.strategyDraft && (
          <>
            <GeneralTab strategy={store.strategyDraft} onChange={setStrategy} showIdentity={false} />
            <LimitsTab strategy={store.strategyDraft} onChange={setStrategy} />
          </>
        )}
        {step.kind === "review" && <ReviewStep schema={schema} original={original} onDone={() => goto(stepIndex + 1)} onError={setError} />}
        {step.kind === "arm" && <ArmStep onFinish={() => { clearWizardDraft(); navigate("/"); }} />}
      </section>

      <div className="dirty-bar">
        <button type="button" className="btn-secondary" onClick={() => { clearWizardDraft(); navigate("/"); }}>Exit setup</button>
        <button type="button" className="btn-secondary" disabled={stepIndex === 0} onClick={() => goto(stepIndex - 1)}>Back</button>
        {step.kind !== "arm" && (
          <button type="button" className="btn-primary" disabled={step.kind === "archetype" && !store.strategyDraft && store.legacyMode === null}
            onClick={() => goto(stepIndex + 1)}>
            {step.kind === "review" ? "Save →" : "Next"}
          </button>
        )}
      </div>
    </>
  );
}

function PreflightStep() {
  const [meta, setMeta] = useState<BotMeta | null>(null);
  useEffect(() => { fetchMeta().then(setMeta).catch(() => {}); }, []);
  const check = (ok: boolean, label: string, detail?: string) => (
    <div className="preflight-row">
      <span className={ok ? "v good" : "v bad"}>{ok ? "✓" : "✗"}</span>
      <span>{label}{detail ? ` — ${detail}` : ""}</span>
    </div>
  );
  if (!meta) return <div className="tree-empty">checking…</div>;
  return (
    <>
      <p className="desc">This wizard configures your build profile and a farming strategy. Nothing is written until the Review step.</p>
      {check(meta.gameAttached, "Game attached", meta.gameAttached ? undefined : "start PoE and relaunch the bot")}
      {check(meta.gateAvailable, "Game-state gate available")}
      {check(meta.gameState === "InGame", "In-world", meta.gameState)}
      {check(true, "Bot version", meta.botVersion)}
    </>
  );
}

function CharacterStep() {
  const status = useStatusStore((s) => s.status);
  return (
    <>
      <p className="desc">Settings are saved per character; the bot auto-switches profiles when you log in.</p>
      <div className="status-grid">
        <div className="status-row"><span className="k">Character</span><span className="v">{status?.character || "(log in to a character)"}</span></div>
        <div className="status-row"><span className="k">League</span><span className="v">{status?.league || "—"}</span></div>
        <div className="status-row"><span className="k">Profile</span><span className="v">{status?.profile || "—"}</span></div>
      </div>
    </>
  );
}

function ArchetypeStep({ onError }: { onError: (e: string) => void }) {
  const [templates, setTemplates] = useState<StrategyTemplate[]>([]);
  const legacyMode = useWizardStore((s) => s.legacyMode);
  const strategyDraft = useWizardStore((s) => s.strategyDraft);
  const [busy, setBusy] = useState(false);

  useEffect(() => { listTemplates().then((t) => setTemplates(t.templates)).catch((e) => onError(String(e))); }, [onError]);

  const chooseTemplate = async (templateId: string) => {
    setBusy(true);
    try {
      // Create the backing document now (with a fresh id); wizard edits it in memory and the
      // Review step saves + activates. Abandoning setup leaves an unconfigured draft strategy
      // the user can delete from the Strategies tab.
      const doc = await createStrategy({ fromTemplate: templateId });
      useWizardStore.setState({ strategyDraft: doc, legacyMode: null });
    } catch (e) {
      onError(String(e));
    } finally {
      setBusy(false);
    }
  };

  const chooseLegacy = (mode: number) => useWizardStore.setState({ legacyMode: mode, strategyDraft: null });

  return (
    <>
      <p className="desc">Pick what to farm. Map-farming archetypes build a strategy document; Blight and Simulacrum use their own settings pages.</p>
      <div className="archetype-grid">
        {templates.map((t) => (
          <button key={t.templateId} type="button" disabled={busy}
            className={`archetype-card ${strategyDraft && !legacyMode ? "" : ""}`}
            onClick={() => chooseTemplate(t.templateId)}>
            <div className="archetype-name">{t.name}</div>
            <div className="desc">{t.description}</div>
          </button>
        ))}
        <button type="button" className={`archetype-card ${legacyMode === 5 ? "chosen" : ""}`} onClick={() => chooseLegacy(5)}>
          <div className="archetype-name">Blight</div>
          <div className="desc">Repeat Blight-ravaged maps. Configured on the Settings → Blight page.</div>
        </button>
        <button type="button" className={`archetype-card ${legacyMode === 6 ? "chosen" : ""}`} onClick={() => chooseLegacy(6)}>
          <div className="archetype-name">Simulacrum</div>
          <div className="desc">Run Simulacrums. Configured on the Settings → Simulacrum page.</div>
        </button>
      </div>
      {strategyDraft && <div className="v good archetype-selected">Selected strategy: {strategyDraft.identity.name}</div>}
      {legacyMode !== null && <div className="v good archetype-selected">Selected mode: {legacyMode === 5 ? "Blight" : "Simulacrum"}</div>}
    </>
  );
}

function ReviewStep({ schema, original, onDone, onError }: {
  schema: Schema; original: Settings | null; onDone: () => void; onError: (e: string) => void;
}) {
  const store = useWizardStore();
  const [saving, setSaving] = useState(false);
  const [fieldErrors, setFieldErrors] = useState<FieldError[]>([]);
  const [saved, setSaved] = useState(false);

  // Diff by schema-field paths — those are exactly the settable leaves the PATCH endpoint
  // accepts. A generic recursive diff would emit sub-paths of complex settings (e.g.
  // skills.slots) that the patcher rejects as non-nested.
  const settingsOps = (): PatchOp[] => {
    if (!store.settingsDraft || !original) return [];
    const ops: PatchOp[] = [];
    for (const field of schema.fields) {
      const path = fieldPath(field);
      const value = pathGet(store.settingsDraft, path);
      if (!sameValue(value, pathGet(original, path))) ops.push({ path, value });
    }
    return ops;
  };

  const save = async () => {
    setSaving(true);
    setFieldErrors([]);
    try {
      const ops = settingsOps();
      if (ops.length > 0) await patchSettings(ops, store.settingsVersion);

      if (store.strategyDraft) {
        await saveStrategy(store.strategyDraft.identity.id, store.strategyDraft);
        await activateStrategy(store.strategyDraft.identity.id);
      } else if (store.legacyMode !== null) {
        await controlMode(store.legacyMode, true);
      }
      setSaved(true);
      onDone();
    } catch (e) {
      if (e instanceof ApiError && e.status === 422 && e.body && typeof e.body === "object" && "errors" in e.body) {
        const body = e.body as { errors: unknown };
        if (Array.isArray(body.errors) && typeof body.errors[0] === "object") setFieldErrors(body.errors as FieldError[]);
        else onError(Array.isArray(body.errors) ? (body.errors as string[]).join("; ") : String(e));
      } else {
        onError(String(e));
      }
    } finally {
      setSaving(false);
    }
  };

  const ops = settingsOps();
  return (
    <>
      <p className="desc">Two artifacts will be written:</p>
      <div className="review-block">
        <strong>Build profile</strong> — {ops.length} setting change{ops.length === 1 ? "" : "s"} applied to the current character.
      </div>
      <div className="review-block">
        {store.strategyDraft
          ? <><strong>Strategy</strong> — "{store.strategyDraft.identity.name}" saved and activated.</>
          : store.legacyMode !== null
            ? <><strong>Mode</strong> — {store.legacyMode === 5 ? "Blight" : "Simulacrum"} activated (configure its settings on the Settings page).</>
            : <span className="v warn">No farming target chosen — go back to the Strategy step.</span>}
      </div>
      {fieldErrors.map((fe) => <div className="v bad" key={fe.path}>✗ {fe.path}: {fe.message}</div>)}
      <button type="button" className="btn-primary" disabled={saving || saved} onClick={save}>
        {saving ? "Saving…" : saved ? "Saved ✓" : "Save & continue"}
      </button>
    </>
  );
}

function ArmStep({ onFinish }: { onFinish: () => void }) {
  const status = useStatusStore((s) => s.status);
  const [result, setResult] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const arm = async () => {
    setBusy(true);
    try {
      const r = await controlArm();
      setResult(r.warnings.length > 0 ? `Armed with warnings: ${r.warnings.join("; ")}` : "Armed.");
    } catch (e) {
      if (e instanceof ApiError && e.body && typeof e.body === "object" && "reasons" in e.body) {
        setResult(`Cannot arm: ${(e.body as { reasons: string[] }).reasons.join("; ")}`);
      } else {
        setResult(String(e));
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <p className="desc">Before arming, confirm in-game:</p>
      <ul className="arm-checklist">
        <li>Your character is in the hideout.</li>
        <li>Supplies (maps, scarabs) are in the named stash tab.</li>
        <li>The map device / atlas node is set up.</li>
      </ul>
      <div className="arm-hint">The bot only acts while PoE is the foreground window{status?.foreground ? "" : " — PoE is not focused right now"}.</div>
      {result && <div className="v">{result}</div>}
      <div className="wizard-arm-actions">
        <button type="button" className="btn-primary" disabled={busy} onClick={arm}>Arm now</button>
        <button type="button" className="btn-secondary" onClick={onFinish}>Finish without arming</button>
      </div>
    </>
  );
}

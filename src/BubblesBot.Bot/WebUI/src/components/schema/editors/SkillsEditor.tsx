import { useState } from "react";
import type { LiveSkill, SkillSlot, SlotProfile } from "../../../api/types";
import { useStatusStore } from "../../../state/statusStore";
import { KeycodeButton } from "./KeycodeButton";

const ROLE_NAMES = ["Disabled", "Walk", "Dash", "Attack", "SelfBuff", "Channel", "Aura", "Mark"];
const ROLE_DASH = 2;

// PoE's default skill-bar slot → key binding. Slots 0-7 are the always-visible hotbar
// (Left/Mid/Right click + QWERT); 8+ are modifier-bound extras with no default.
const SLOT_DEFAULT_KEY: { vk: number; label: string }[] = [
  { vk: 0x01, label: "LMB" },
  { vk: 0x04, label: "MMB" },
  { vk: 0x02, label: "RMB" },
  { vk: 0x51, label: "Q" },
  { vk: 0x57, label: "W" },
  { vk: 0x45, label: "E" },
  { vk: 0x52, label: "R" },
  { vk: 0x54, label: "T" },
];

interface Props {
  value: SlotProfile<SkillSlot> | undefined;
  onChange: (value: SlotProfile<SkillSlot>) => void;
}

export function SkillsEditor({ value, onChange }: Props) {
  const profile: SlotProfile<SkillSlot> = value ?? { slots: [] };
  const slots = profile.slots ?? [];
  const [showDetected, setShowDetected] = useState(false);

  const setSlots = (next: SkillSlot[]) => onChange({ ...profile, slots: next });
  const updateSlot = (index: number, patch: Partial<SkillSlot>) =>
    setSlots(slots.map((slot, i) => (i === index ? { ...slot, ...patch } : slot)));

  const importDetected = (entry: LiveSkill) => {
    const def = SLOT_DEFAULT_KEY[entry.barSlot] ?? { vk: 0, label: "" };
    const name = entry.name?.length
      ? entry.name
      : def.label
        ? `${def.label} skill`
        : `Skill ${entry.barSlot}`;
    setSlots([
      ...slots,
      {
        name,
        vk: def.vk,
        role: 0,
        canCrossGaps: false,
        minCastIntervalMs: 100,
        maxRangeGrid: 30,
        chargeCount: Math.max(1, entry.maxUses || 1),
        chargeRechargeMs: 3000,
        gemId: entry.gemId,
      },
    ]);
  };

  return (
    <div className="skills-editor">
      <div className="skills-list">
        {slots.map((slot, index) => (
          <SkillRow
            key={index}
            slot={slot}
            onPatch={(patch) => updateSlot(index, patch)}
            onRemove={() => setSlots(slots.filter((_, i) => i !== index))}
          />
        ))}
      </div>
      {showDetected && (
        <DetectedSkillsPanel
          importedGemIds={new Set(slots.map((s) => Number(s.gemId)).filter(Boolean))}
          onImport={importDetected}
          onClose={() => setShowDetected(false)}
        />
      )}
      <div className="skill-add-row">
        <button type="button" className="skill-add" onClick={() => setShowDetected(true)}>
          + add skill
        </button>
        <button
          type="button"
          className="skill-add"
          title="Add an empty skill slot to fill in by hand (use 'add skill' to import detected skills)"
          onClick={() =>
            setSlots([
              ...slots,
              { name: "New", vk: 0, role: 0, canCrossGaps: false, minCastIntervalMs: 100, maxRangeGrid: 30, chargeCount: 1, chargeRechargeMs: 3000, gemId: 0 },
            ])
          }
        >
          + blank slot
        </button>
      </div>
    </div>
  );
}

function SkillRow({ slot, onPatch, onRemove }: {
  slot: SkillSlot;
  onPatch: (patch: Partial<SkillSlot>) => void;
  onRemove: () => void;
}) {
  return (
    <div className="skill-row">
      <input
        type="text"
        className="skill-name"
        placeholder="Name"
        value={slot.name ?? ""}
        onChange={(e) => onPatch({ name: e.target.value })}
      />
      <KeycodeButton value={slot.vk} onChange={(vk) => onPatch({ vk })} />
      <select value={slot.role} onChange={(e) => onPatch({ role: parseInt(e.target.value, 10) })}>
        {ROLE_NAMES.map((role, i) => (
          <option key={i} value={i}>{role}</option>
        ))}
      </select>
      {Number(slot.role) === ROLE_DASH && (
        <label className="skill-flag">
          <input
            type="checkbox"
            checked={!!slot.canCrossGaps}
            onChange={(e) => onPatch({ canCrossGaps: e.target.checked })}
          />
          {" cross gaps"}
        </label>
      )}
      <NumField label="interval ms" value={slot.minCastIntervalMs} onChange={(v) => onPatch({ minCastIntervalMs: v })} />
      <NumField label="range" value={slot.maxRangeGrid} onChange={(v) => onPatch({ maxRangeGrid: v })} />
      <NumField label="gemId" value={Number(slot.gemId ?? 0)} onChange={(v) => onPatch({ gemId: v })} />
      {Number(slot.role) === ROLE_DASH && (
        <>
          <NumField label="charges" value={slot.chargeCount} onChange={(v) => onPatch({ chargeCount: v })} />
          <NumField label="recharge ms" value={slot.chargeRechargeMs} onChange={(v) => onPatch({ chargeRechargeMs: v })} />
        </>
      )}
      <button type="button" className="skill-remove" title="Remove" onClick={onRemove}>×</button>
    </div>
  );
}

export function NumField({ label, value, onChange, float = false }: {
  label: string;
  value: number;
  onChange: (value: number) => void;
  float?: boolean;
}) {
  return (
    <label className="skill-num">
      {label}{" "}
      <input
        type="number"
        step={float ? 0.05 : 1}
        value={value}
        onChange={(e) => onChange(float ? parseFloat(e.target.value) || 0 : parseInt(e.target.value, 10) || 0)}
      />
    </label>
  );
}

function DetectedSkillsPanel({ importedGemIds, onImport, onClose }: {
  importedGemIds: Set<number>;
  onImport: (entry: LiveSkill) => void;
  onClose: () => void;
}) {
  const liveSkills = useStatusStore((s) => s.status?.liveSkills);

  if (!liveSkills || liveSkills.length === 0) {
    return (
      <div className="skills-detected-wrap">
        <div className="detected-empty">(no skills detected — log into a character)</div>
        <button type="button" className="skill-add detected-close" onClick={onClose}>× close</button>
      </div>
    );
  }

  const visible = liveSkills.filter((e) => e.barSlot < 8);
  const extras = liveSkills.filter((e) => e.barSlot >= 8);

  return (
    <div className="skills-detected-wrap">
      <div className="detected-head">Detected skills (visible bar)</div>
      <DetectedGrid entries={visible} importedGemIds={importedGemIds} onImport={onImport} />
      {extras.length > 0 && (
        <>
          <div className="detected-head detected-head-sub">Extras (modifier-bound / duplicates — usually skip)</div>
          <DetectedGrid entries={extras} importedGemIds={importedGemIds} onImport={onImport} />
        </>
      )}
      <button type="button" className="skill-add detected-close" onClick={onClose}>× close</button>
    </div>
  );
}

function DetectedGrid({ entries, importedGemIds, onImport }: {
  entries: LiveSkill[];
  importedGemIds: Set<number>;
  onImport: (entry: LiveSkill) => void;
}) {
  return (
    <div className="detected-grid">
      {entries.map((entry) => {
        const def = SLOT_DEFAULT_KEY[entry.barSlot];
        const keyLabel = def?.label ?? `slot ${entry.barSlot}`;
        const name = entry.name || `Skill #${entry.gemId}`;
        const imported = importedGemIds.has(Number(entry.gemId));
        return (
          <div className="detected-card" key={`${entry.barSlot}:${entry.gemId}`}>
            <div className="d-key">{keyLabel}</div>
            <div className="d-name">
              {name} <span className={`d-ready ${entry.isReady ? "good" : "warn"}`}>{entry.isReady ? "✓" : "•"}</span>
            </div>
            <div className="d-meta">gem {entry.gemId}{entry.maxUses ? ` · ${entry.maxUses}x` : ""}</div>
            {imported ? (
              <span className="d-imported">imported</span>
            ) : (
              <button type="button" className="d-import" onClick={() => onImport(entry)}>+ import</button>
            )}
          </div>
        );
      })}
    </div>
  );
}

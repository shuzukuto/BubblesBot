import { useState } from "react";
import { ApiError, controlArm, controlDisarm, controlMode, type ControlResponse } from "../../api/client";
import { useStatusStore } from "../../state/statusStore";

const MODES = [
  { value: 0, label: "Overlay / manual" },
  { value: 4, label: "Map farming" },
  { value: 5, label: "Blight" },
  { value: 6, label: "Simulacrum" },
];

/**
 * Arm/disarm + mode switch through the control API. Arming here is the same persisted flag
 * as the in-game Insert hotkey; the bot still only ACTS while PoE is the foreground window —
 * surfaced as a warning rather than a failure.
 */
export function ArmControl() {
  const status = useStatusStore((s) => s.status);
  const [messages, setMessages] = useState<{ text: string; bad: boolean }[]>([]);
  const [busy, setBusy] = useState(false);

  const armed = !!status?.armed;
  const activeMode = Number(status?.activeMode ?? 0);

  const run = async (action: () => Promise<ControlResponse>) => {
    setBusy(true);
    setMessages([]);
    try {
      const result = await action();
      setMessages(result.warnings.map((w) => ({ text: w, bad: false })));
    } catch (e) {
      if (e instanceof ApiError && e.body && typeof e.body === "object") {
        const body = e.body as ControlResponse;
        setMessages((body.reasons ?? [String(e)]).map((r) => ({ text: r, bad: true })));
      } else {
        setMessages([{ text: String(e), bad: true }]);
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="card">
      <h2>Control</h2>
      <div className="arm-row">
        <button
          type="button"
          className={armed ? "btn-secondary arm-btn" : "btn-primary arm-btn"}
          disabled={busy || !status}
          onClick={() => run(armed ? controlDisarm : () => controlArm())}
        >
          {armed ? "Disarm" : "Arm"}
        </button>
        <select
          value={activeMode}
          disabled={busy || armed || !status}
          title={armed ? "Disarm before switching modes" : undefined}
          onChange={(e) => run(() => controlMode(parseInt(e.target.value, 10)))}
        >
          {MODES.map((mode) => (
            <option key={mode.value} value={mode.value}>{mode.label}</option>
          ))}
        </select>
        <span className={`v ${armed ? (status?.shouldAct ? "good" : "warn") : ""}`}>
          {status?.stateLabel ?? "—"}
        </span>
      </div>
      {!status?.foreground && armed && (
        <div className="arm-hint">PoE is not focused — the bot only acts while PoE is the foreground window.</div>
      )}
      {messages.map((message, i) => (
        <div key={i} className={`arm-hint ${message.bad ? "v bad" : ""}`}>{message.text}</div>
      ))}
    </section>
  );
}

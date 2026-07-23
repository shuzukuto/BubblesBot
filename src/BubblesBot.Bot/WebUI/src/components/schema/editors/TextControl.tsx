import { useState } from "react";
import type { SchemaField } from "../../../api/types";

interface Props {
  field?: SchemaField;
  value: string;
  onChange: (value: string) => void;
}

export function TextControl({ field, value, onChange }: Props) {
  const [scanning, setScanning] = useState(false);
  const [foundBuffs, setFoundBuffs] = useState<string[] | null>(null);

  const handleScan = async () => {
    setScanning(true);
    try {
      const res = await fetch("/api/game/buffs");
      if (res.ok) {
        const buffs: string[] = await res.json();
        setFoundBuffs(buffs);
      }
    } finally {
      setScanning(false);
    }
  };

  const isBuffField = field?.name === "requiredMapBuffName";

  if (!isBuffField) {
    return <input type="text" value={value} onChange={(e) => onChange(e.target.value)} />;
  }

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "4px" }}>
      <div style={{ display: "flex", gap: "8px" }}>
        <input type="text" value={value} onChange={(e) => onChange(e.target.value)} style={{ flex: 1, minWidth: 0 }} />
        <button type="button" onClick={handleScan} disabled={scanning} style={{ whiteSpace: "nowrap" }}>
          {scanning ? "Scanning..." : "Scan Buffs"}
        </button>
      </div>
      {foundBuffs && foundBuffs.length > 0 && (
        <div style={{ display: "flex", flexWrap: "wrap", gap: "4px", marginTop: "4px" }}>
          {foundBuffs.map(b => (
            <button
              key={b}
              type="button"
              onClick={() => { onChange(b); setFoundBuffs(null); }}
              style={{ fontSize: "11px", padding: "2px 6px", cursor: "pointer", background: "var(--bg-mid)", border: "1px solid var(--border-light)", color: "var(--text-bright)", borderRadius: "4px" }}
            >
              {b}
            </button>
          ))}
        </div>
      )}
      {foundBuffs && foundBuffs.length === 0 && (
        <div style={{ fontSize: "12px", color: "var(--text-dim)", fontStyle: "italic" }}>
          No active buffs found. Make sure the game is running and player is loaded.
        </div>
      )}
    </div>
  );
}

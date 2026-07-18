import { useMemo } from "react";
import type { FarmingStrategy, MechanicType } from "../../api/strategy";
import { useStatusStore } from "../../state/statusStore";

/**
 * Renders the FIXED map-farming lifecycle as a flowchart, with the strategy's enabled
 * mechanics drawn into their host phase. It is a rendering of the runtime's phases, never a
 * user-wired graph. When the bot is live in mode 4, the node matching the current
 * MapRunPhase (loop.lifecyclePhase) is highlighted.
 *
 * Node click opens that node's policy editor via onEditMechanic (mechanic nodes only).
 */

interface FlowNode {
  key: string;
  label: string;
  /** MapRunPhase enum name(s) this node lights up for. */
  phases: string[];
  lane: "hideout" | "inmap";
  mechanic?: MechanicType;
  enabled?: boolean;
  badge?: string;
}

interface Props {
  strategy: FarmingStrategy;
  onEditMechanic?: (type: MechanicType) => void;
}

export function FlowchartView({ strategy, onEditMechanic }: Props) {
  const lifecyclePhase = useStatusStore((s) => {
    const loop = s.status?.loop;
    return loop && s.status?.activeMode === 4 ? String(loop.lifecyclePhase) : null;
  });

  const nodes = useMemo(() => buildNodes(strategy), [strategy]);
  const hideout = nodes.filter((n) => n.lane === "hideout");
  const inmap = nodes.filter((n) => n.lane === "inmap");

  const renderNode = (node: FlowNode) => {
    const active = lifecyclePhase != null && node.phases.includes(lifecyclePhase);
    const clickable = node.mechanic && onEditMechanic;
    return (
      <button
        key={node.key}
        type="button"
        className={`flow-node ${active ? "active" : ""} ${node.enabled === false ? "off" : ""} ${clickable ? "clickable" : ""}`}
        disabled={!clickable}
        onClick={() => node.mechanic && onEditMechanic?.(node.mechanic)}
      >
        <span className="flow-node-label">{node.label}</span>
        {node.enabled === false && <span className="flow-badge">off</span>}
        {node.badge && <span className="flow-badge good">{node.badge}</span>}
      </button>
    );
  };

  return (
    <div className="flowchart">
      <div className="flow-lane-label">Hideout</div>
      <div className="flow-lane">
        {hideout.map((n, i) => (
          <span className="flow-cell" key={n.key}>
            {renderNode(n)}
            {i < hideout.length - 1 && <span className="flow-arrow">→</span>}
          </span>
        ))}
      </div>
      <div className="flow-connector">↓ enter map</div>
      <div className="flow-lane-label">In map</div>
      <div className="flow-lane wrap">
        {inmap.map((n, i) => (
          <span className="flow-cell" key={n.key}>
            {renderNode(n)}
            {i < inmap.length - 1 && <span className="flow-arrow">→</span>}
          </span>
        ))}
      </div>
      <div className="flow-connector">↺ portal out → deposit → repeat</div>
    </div>
  );
}

function buildNodes(strategy: FarmingStrategy): FlowNode[] {
  const enabled = (type: MechanicType) => strategy.mechanics.find((m) => m.type === type)?.enabled ?? false;
  const nodes: FlowNode[] = [
    { key: "prep", label: "Withdraw supplies", phases: ["Preparation"], lane: "hideout" },
    { key: "deposit", label: "Deposit loot", phases: ["Deposit"], lane: "hideout" },
    { key: "device", label: "Map device", phases: ["Device"], lane: "hideout" },
    { key: "entry", label: "Enter", phases: ["Entry"], lane: "hideout" },
    { key: "boss", label: "Boss hunt", phases: ["BossMechanics"], lane: "inmap",
      badge: strategy.completion.requireBossKill ? "gated" : undefined },
    { key: "sweep", label: "Clear + explore", phases: ["Clear"], lane: "inmap" },
    { key: "shrines", label: "Shrines", phases: ["Clear"], lane: "inmap", mechanic: "shrines", enabled: enabled("shrines") },
    { key: "altars", label: "Eldritch altars", phases: ["Clear"], lane: "inmap", mechanic: "eldritchAltars", enabled: enabled("eldritchAltars") },
    { key: "memoryTears", label: "Memory tears", phases: ["Clear"], lane: "inmap", mechanic: "memoryTears", enabled: enabled("memoryTears") },
    { key: "strongboxes", label: "Strongboxes", phases: ["Clear"], lane: "inmap", mechanic: "strongboxes", enabled: enabled("strongboxes") },
    { key: "ritual", label: "Ritual chain", phases: ["Clear", "BossMechanics"], lane: "inmap", mechanic: "ritual", enabled: enabled("ritual") },
    { key: "completion", label: "Finish + loot", phases: ["Completion"], lane: "inmap" },
    { key: "exit", label: "Exit", phases: ["Exit", "Report"], lane: "inmap" },
  ];
  return nodes;
}

import { useCallback, useEffect, useState, useMemo } from "react";
import { ApiError, fetchSchema, fetchSettings, patchSettings, type FieldError, type PatchOp, type SettingsEnvelope } from "../api/client";
import type { Schema, Settings } from "../api/types";
import { fieldPath, pathGet, pathSet, sameValue } from "../lib/paths";
import { SchemaForm } from "../components/schema/SchemaForm";

/**
 * Draft-based settings editor. Edits accumulate locally; Save sends a PATCH containing only
 * the dirty schema paths with the last-seen version — a stale page gets a 409 (rebased, edits
 * kept) instead of clobbering concurrent changes, and out-of-range values get per-field 422s.
 */
export default function SettingsPage() {
  const [schema, setSchema] = useState<Schema | null>(null);
  const [original, setOriginal] = useState<Settings | null>(null);
  const [version, setVersion] = useState(0);
  const [draft, setDraft] = useState<Settings | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<FieldError[]>([]);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    Promise.all([fetchSchema(), fetchSettings()])
      .then(([loadedSchema, envelope]) => {
        setSchema(loadedSchema);
        setOriginal(envelope.settings);
        setVersion(envelope.version);
        setDraft(envelope.settings);
      })
      .catch((e) => setError(String(e)));
  }, []);

  const onChange = useCallback((path: string[], value: unknown) => {
    setDraft((current) => (current ? pathSet(current, path, value) : current));
  }, []);

  const dirty = draft !== null && original !== null && !sameValue(draft, original);

  const dirtyOps = (): PatchOp[] => {
    if (!schema || !draft || !original) return [];
    const ops: PatchOp[] = [];
    for (const field of schema.fields) {
      const path = fieldPath(field);
      const draftValue = pathGet(draft, path);
      if (!sameValue(draftValue, pathGet(original, path))) ops.push({ path, value: draftValue });
    }
    return ops;
  };

  const save = async () => {
    const ops = dirtyOps();
    if (ops.length === 0) return;
    setSaving(true);
    setError(null);
    setFieldErrors([]);
    try {
      const applied = await patchSettings(ops, version);
      setOriginal(applied.settings);
      setVersion(applied.version);
      setDraft(applied.settings);
    } catch (e) {
      if (e instanceof ApiError && e.status === 409 && e.body && typeof e.body === "object") {
        const fresh = e.body as SettingsEnvelope;
        setOriginal(fresh.settings);
        setVersion(fresh.version);
        setError("Settings changed elsewhere (hotkey/profile switch) — review your edits and save again.");
      } else if (e instanceof ApiError && e.status === 422 && e.body && typeof e.body === "object") {
        setFieldErrors((e.body as { errors: FieldError[] }).errors ?? []);
        setError("Some values were rejected:");
      } else {
        setError(String(e));
      }
    } finally {
      setSaving(false);
    }
  };

  const categories = useMemo(() => {
    if (!schema) return [];
    return Array.from(new Set(schema.fields.map(f => f.category)));
  }, [schema]);
  const [activeCategory, setActiveCategory] = useState<string | null>(null);

  // Set default active category once loaded
  useEffect(() => {
    if (categories.length > 0 && !activeCategory) {
      setActiveCategory(categories[0]);
    }
  }, [categories, activeCategory]);

  if (error && !schema) return <section className="card"><h2>Settings</h2><div className="v bad">{error}</div></section>;
  if (!schema || !draft || !original) return <section className="card"><h2>Settings</h2><div className="tree-empty">loading…</div></section>;

  return (
    <>
      <section className="card settings-card">
        <h2>Settings</h2>
        <div className="settings-layout">
          <div className="settings-sidebar">
            {categories.map(c => (
              <button
                key={c}
                type="button"
                className={`settings-tab ${c === activeCategory ? 'active' : ''}`}
                onClick={() => setActiveCategory(c)}
              >
                {c.toUpperCase()}
              </button>
            ))}
          </div>
          <div className="settings-content">
            <SchemaForm 
              fields={schema.fields} 
              values={draft} 
              saved={original} 
              onChange={onChange} 
              categories={activeCategory ? [activeCategory] : undefined}
            />
          </div>
        </div>
      </section>
      {(dirty || fieldErrors.length > 0) && (
        <div className="dirty-bar">
          <div className="dirty-messages">
            <span className="dirty-note">{dirty ? "Unsaved changes" : ""}</span>
            {error && <span className="v bad dirty-error">{error}</span>}
            {fieldErrors.map((fe) => (
              <span className="v bad dirty-error" key={fe.path}>{fe.path}: {fe.message}</span>
            ))}
          </div>
          <button type="button" className="btn-secondary" onClick={() => { setDraft(original); setError(null); setFieldErrors([]); }} disabled={saving}>
            Discard
          </button>
          <button type="button" className="btn-primary" onClick={save} disabled={saving || !dirty}>
            {saving ? "Saving…" : "Save"}
          </button>
        </div>
      )}
    </>
  );
}

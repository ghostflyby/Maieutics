/**
 * Plugins-surface presentation (ADR 0038 stage 4): pure pick models over
 * `GET /v1/plugins` and approved gateway page links. No vscode and no network imports — the QuickPick loop and
 * the renderer bridge stay injectable and unit-tested.
 */

import type { PluginInfo } from "./protocol.ts";

/** Approval states the surface reports; unknown states read as pending. */
export type PluginApproval = "Exempt" | "Approved" | "PendingApproval" | "BlockedByDependency";

/** One QuickPick item on the plugins surface. */
export interface PluginPick {
  readonly pluginId: string;
  readonly label: string;
  readonly description: string;
  readonly detail?: string;
  /** Present when picking the item should publish the plugin's declarative form. */
  readonly publishForm: boolean;
  /** Present when picking the item should open the plugin's gateway page. */
  readonly pageUrl?: string;
}

const APPROVAL_BADGE: Record<string, string> = {
  Exempt: "$(shield) exempt",
  Approved: "$(check) approved",
  PendingApproval: "$(shield) pending approval",
  BlockedByDependency: "$(shield) blocked by dependency",
};

function approvalBadge(state: string): string {
  return APPROVAL_BADGE[state] ?? "$(shield) pending approval";
}

/** Builds the pick list: forms first (actionable), then pages, then the rest.
 * Picks whose plugin is not approved carry no action. */
export function pluginPicks(plugins: readonly PluginInfo[]): PluginPick[] {
  const ranked = [...plugins].sort((left, right) => {
    const rank = (plugin: PluginInfo): number => plugin.form ? 0 : plugin.pageUrl ? 1 : 2;
    return rank(left) - rank(right) || left.id.localeCompare(right.id);
  });
  return ranked.map((plugin) => {
    const approved = plugin.approvalState === "Approved" || plugin.approvalState === "Exempt";
    const actionable = approved && (plugin.form !== null && plugin.form !== undefined ||
      (plugin.pageUrl !== null && plugin.pageUrl !== undefined && plugin.pageUrl.length > 0));
    return {
      pluginId: plugin.id,
      label: `$(${actionable ? "plug" : "shield"}) ${plugin.name || plugin.id}`,
      description: approvalBadge(plugin.approvalState),
      ...(plugin.formError
        ? { detail: plugin.formError }
        : plugin.pageUrl
        ? { detail: plugin.pageUrl }
        : {}),
      publishForm: actionable && plugin.form !== null && plugin.form !== undefined,
      ...(plugin.pageUrl && approved ? { pageUrl: plugin.pageUrl } : {}),
    };
  });
}

export const PluginsEmptyHint = "No plugins discovered.";

/** Validate an absolute loopback plugin-gateway URL before opening it. */
export function pluginPageUrl(value: string): string | undefined {
  try {
    const url = new URL(value);
    if (
      !["http:", "https:"].includes(url.protocol) ||
      !["127.0.0.1", "localhost", "[::1]"].includes(url.hostname)
    ) return undefined;
    const parts = url.pathname.split("/").filter(Boolean);
    return parts.length >= 3 && parts[1] === "plugins" ? value : undefined;
  } catch {
    return undefined;
  }
}

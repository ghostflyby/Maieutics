/**
 * Plugins-surface presentation (ADR 0038 stage 4): pure pick models over
 * `GET /v1/plugins` and the `maieutics/iframe` view state a plugin page
 * renders through. No vscode and no network imports — the QuickPick loop and
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

/** The `maieutics/iframe` view announcement (ADR 0038 §6.4): a native view-family
 * model whose state is the gateway URL. The kernel-side family covers plugin pages;
 * the extension composes the same shape when embedding is requested. */
export interface IframeViewState {
  readonly modelId: string;
  readonly viewFamily: "maieutics/iframe";
  readonly version: "1.0";
  readonly state: { url: string };
}

/** Builds an iframe view state for a gateway page URL. The URL must be a
 * loopback http gateway link (`/<entrance-token>/plugins/<id>/…`) — https pages
 * may link out, but only the token-bearing gateway embeds. */
export function iframeViewState(pageUrl: string, modelId: string): IframeViewState | undefined {
  if (typeof pageUrl !== "string" || pageUrl.length === 0) return undefined;
  const parsed = new URL(pageUrl, "http://127.0.0.1/");
  if (parsed.protocol !== "http:" && parsed.protocol !== "https:") return undefined;
  const host = parsed.hostname;
  const loopback = host === "127.0.0.1" || host === "localhost" || host === "::1";
  if (!loopback) return undefined;
  // Gateway projection: /<entrance-token>/plugins/<pluginId>/… — the token is
  // segment one; the plugin root is the segment after "plugins".
  const segments = parsed.pathname.split("/").filter((segment) => segment.length > 0);
  if (!segments.includes("plugins")) return undefined;
  return {
    modelId,
    viewFamily: "maieutics/iframe",
    version: "1.0",
    state: { url: pageUrl },
  };
}

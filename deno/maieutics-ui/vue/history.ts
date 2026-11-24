import messages from "../locales/en.json" with { type: "json" };
import {
  type ConversationLocalizer,
  formatConversationMessage,
  historyErrorMessageKey,
} from "../localization.ts";
import {
  computed,
  defineComponent,
  h,
  nextTick,
  onBeforeUnmount,
  onScopeDispose,
  type PropType,
  ref,
  shallowRef,
  watch,
} from "vue";
import {
  defaultRangeExtractor,
  elementScroll,
  measureElement,
  useVirtualizer,
} from "@tanstack/vue-virtual";
import { ConversationRuntime, ConversationViewState, isActivity } from "../core/runtime.ts";
import { FrozenScrollLayout } from "../core/scroll_layout.ts";
import {
  type ConversationEntry,
  type ConversationRow,
  type PageLoadProgress,
  type TextContent,
  type TextPoint,
} from "../core/types.ts";

import type {
  ConversationAnchorRestoration,
  ConversationBlockSurface,
  ConversationHost,
} from "./contracts.ts";
import { useBinding } from "./binding.ts";

const labels: Record<ConversationEntry["kind"], string> = {
  input: messages.user,
  message: messages.assistant,
  tool: messages.tool,
  "reasoning-summary": messages.reasoning,
  display: messages.display,
  "child-run": messages.childRun,
  diagnostic: messages.diagnostic,
  unavailable: messages.unavailable,
};

const OutputBlockHost = defineComponent({
  name: "OutputBlockHost",
  props: {
    runtime: { type: Object as PropType<ConversationRuntime>, required: true },
    view: { type: Object as PropType<ConversationViewState>, required: true },
    entryId: { type: String, required: true },
    index: { type: Number, required: true },
    joinsPreviousTool: { type: Boolean, default: false },
    joinsNextTool: { type: Boolean, default: false },
    measure: { type: Function as PropType<(element: HTMLElement) => void>, required: true },
    host: { type: Object as PropType<ConversationHost>, required: true },
    localize: {
      type: Function as PropType<ConversationLocalizer>,
      default: formatConversationMessage,
    },
  },
  setup(props) {
    const entry = shallowRef(props.runtime.projection.find(props.entryId));
    const content = shallowRef<TextContent>();
    const body = shallowRef<HTMLElement>();
    let surface: ConversationBlockSurface | undefined;
    let factory: ConversationHost["mountBlock"];
    let drawing: AbortController | undefined;
    const release = () => {
      drawing?.abort();
      const previous = surface;
      surface = undefined;
      previous?.dispose();
    };
    const failure = ref("");
    const expanded = ref(props.view.isExpanded(props.entryId));
    let request: AbortController | undefined;
    const stopEntry = props.runtime.subscribeEntry(props.entryId, () => {
      entry.value = props.runtime.projection.find(props.entryId);
    });
    const stopView = props.view.subscribe(() => {
      expanded.value = props.view.isExpanded(props.entryId);
    });
    const load = async () => {
      request?.abort();
      request = new AbortController();
      const signal = AbortSignal.any([request.signal, props.view.signal]);
      // Keep a mounted surface while the next revision loads, preserving its component state.
      if (!surface) content.value = undefined;
      failure.value = "";
      const current = entry.value;
      if (
        !current || current.kind === "unavailable" ||
        (!props.host.mountBlock && isActivity(current) && !expanded.value)
      ) {
        return;
      }
      try {
        const value = await props.runtime.readContent(current.id, current.contentRevision, signal);
        if (!signal.aborted) content.value = value;
      } catch (error) {
        if (!signal.aborted) failure.value = messages[historyErrorMessageKey(error)];
      }
    };
    watch(() => [entry.value?.contentRevision, entry.value?.kind, expanded.value], load, {
      immediate: true,
    });
    watch(
      [body, content, entry, () => props.host.mountBlock],
      async ([element, value, current, mount]) => {
        if (factory !== mount) {
          release();
          factory = mount;
        }
        if (!element || !value?.presentation) {
          release();
          return;
        }
        if (
          !current || value.revision !== current.contentRevision || !mount
        ) return;
        drawing?.abort();
        drawing = new AbortController();
        const signal = AbortSignal.any([drawing.signal, props.view.signal]);
        try {
          surface ??= mount(element);
          await surface.render(current, value, signal);
        } catch (error) {
          if (!signal.aborted) {
            failure.value = messages[historyErrorMessageKey(error)];
            content.value = { ...value, presentation: undefined };
          }
        }
      },
      { flush: "post" },
    );
    onBeforeUnmount(() => {
      try {
        release();
      } finally {
        request?.abort();
        stopEntry();
        stopView();
      }
    });
    return () => {
      const current = entry.value;
      const attributes = {
        class: ["mui-row", "mui-block", current ? `mui-${current.kind}` : "mui-missing", {
          "mui-tool-joins-previous": props.joinsPreviousTool,
          "mui-tool-joins-next": props.joinsNextTool,
        }],
        "data-index": props.index,
        ref: (element: unknown) => {
          if (element instanceof HTMLElement) props.measure(element);
        },
      };
      if (!current) return h("article", attributes, props.localize(messages.recycled));
      const activity = !props.host.mountBlock && isActivity(current);
      const collapsed = activity && !expanded.value;
      const value = current.kind === "unavailable"
        ? current.preview || props.localize(messages.removed)
        : collapsed
        ? current.preview
        : content.value?.text;
      const rich =
        !!(props.host.mountBlock && content.value?.presentation && current.kind !== "unavailable");
      const writing = current.state === "open";
      return h("article", {
        ...attributes,
        "aria-label": props.localize(labels[current.kind]),
        "data-entry-id": current.id,
        "data-revision": current.contentRevision,
        "data-ordinal": current.ordinal,
      }, [
        !props.joinsPreviousTool || activity || writing
          ? h("header", { class: "mui-block-heading" }, [
            props.joinsPreviousTool
              ? null
              : h("h3", { class: "mui-role" }, props.localize(labels[current.kind])),
            activity
              ? h("button", {
                class: "mui-button mui-expand",
                type: "button",
                "aria-expanded": !collapsed,
                onClick: () => props.view.toggle(current.id),
              }, collapsed ? props.localize(messages.expand) : props.localize(messages.collapse))
              : null,
            writing
              ? h(
                "span",
                { class: "mui-writing", "aria-label": props.localize(messages.writing) },
                props.localize(messages.writing),
              )
              : null,
          ])
          : null,
        rich
          ? h("div", { class: "mui-rendered", ref: body })
          : value !== undefined
          ? h("div", { class: "mui-text", "data-text-entry": current.id }, value)
          : !failure.value
          ? h("div", {
            class: "mui-loading",
            "aria-label": props.localize(messages.loadingContent),
          }, props.localize(messages.loading))
          : null,
        failure.value
          ? h("div", { role: "status", class: "mui-error" }, [
            props.localize(failure.value),
            h(
              "button",
              { class: "mui-button mui-retry", type: "button", onClick: load },
              props.localize(messages.retry),
            ),
          ])
          : null,
      ]);
    };
  },
});

const estimatedHeight = 120;

const historyProps = {
  view: { type: Object as PropType<ConversationViewState>, required: true },
  host: { type: Object as PropType<ConversationHost>, required: true },
  title: { type: String, default: "" },
  localize: {
    type: Function as PropType<ConversationLocalizer>,
    default: formatConversationMessage,
  },
} as const;

/** Independently mountable history. The caller owns the shared view attachment. */
export const ConversationHistory = defineComponent({
  name: "ConversationHistory",
  props: historyProps,
  emits: { ready: () => true, anchorRestored: (_anchor: ConversationAnchorRestoration) => true },
  setup(props, { emit }) {
    return useBinding(() => props.view, (view) => {
      const key = Symbol("history");
      const runtime = view.runtime;
      const lifetime = new AbortController();
      const signal = AbortSignal.any([view.signal, lifetime.signal]);
      const root = ref<HTMLElement>();
      const viewport = ref<HTMLElement>();
      const rows = shallowRef(signal.aborted ? [] : runtime.projection.rows);
      const status = ref(signal.aborted ? messages.closed : "");
      const following = ref(true);
      const frozenLayout = shallowRef<FrozenScrollLayout>();
      let heldPointer: number | undefined;
      let heldMouse = false;
      const initialSelection = view.selection;
      const pins = shallowRef<readonly number[]>(
        initialSelection ? [initialSelection.anchor.ordinal, initialSelection.focus.ordinal] : [],
      );
      const focused = ref<number>();
      const loadErrors = new Map<string, { offset: number; message: string }>();
      const loadVersion = ref(0);
      let viewportReady = false;
      let scanFrame: number | undefined;
      let lastScrollTop = 0;
      let programmaticOffset: number | undefined;
      let rangeRequest: {
        key: string;
        offset: number;
        controller: AbortController;
        progress: PageLoadProgress;
      } | undefined;
      const close = () => {
        rows.value = [];
        status.value = messages.closed;
        if (scanFrame !== undefined) cancelAnimationFrame(scanFrame);
        rangeRequest?.controller.abort();
        frozenLayout.value = undefined;
      };
      signal.addEventListener("abort", close, { once: true });
      let navigation = 0;
      let geometryGeneration = 0;
      const stop = signal.aborted ? () => {} : runtime.subscribe(() => {
        const offset = viewport.value?.scrollTop ?? 0;
        const anchor = virtual.value.getVirtualItems().find((item) =>
          item.start <= offset && item.end > offset && rows.value[item.index]?.type === "entry"
        );
        const key = anchor ? rows.value[anchor.index]?.key : undefined;
        const inside = anchor ? offset - anchor.start : 0;
        const generation = ++geometryGeneration, currentNavigation = navigation;
        rows.value = runtime.projection.rows;
        if (key && !following.value && !frozenLayout.value) {
          nextTick().then(() => {
            if (
              signal.aborted || generation !== geometryGeneration ||
              currentNavigation !== navigation || following.value || frozenLayout.value
            ) return;
            const index = rows.value.findIndex((row) => row.key === key);
            const position = index >= 0
              ? virtual.value.getOffsetForIndex(index, "start")
              : undefined;
            const target = rows.value[index];
            if (position && target?.type === "entry") {
              virtual.value.scrollToOffset(position[0] + inside);
              emit("anchorRestored", {
                reason: "projection",
                entryId: key,
                ordinal: target.ordinal,
                offset: inside,
              });
            }
          }).catch((error) => {
            if (!signal.aborted) status.value = messages[historyErrorMessageKey(error)];
          });
        }
      });
      const stopView = signal.aborted ? () => {} : view.subscribe(() => {
        const selection = view.selection;
        const next = selection ? [selection.anchor.ordinal, selection.focus.ordinal] : [];
        if (
          next.length !== pins.value.length || next.some((ordinal, i) => ordinal !== pins.value[i])
        ) {
          pins.value = next;
        }
      });
      const stopSubscriptions = () => {
        stop();
        stopView();
      };
      signal.addEventListener("abort", stopSubscriptions, { once: true });
      const pinIndexes = computed(() =>
        [...pins.value, ...(focused.value === undefined ? [] : [focused.value])].flatMap(
          (ordinal) => {
            const index = rows.value.findIndex((row) =>
              row.type === "entry" && row.ordinal === ordinal
            );
            return index < 0 ? [] : [index];
          },
        )
      );
      const virtual = useVirtualizer<HTMLElement, HTMLElement>(computed(() => {
        const pinned = pinIndexes.value;
        return {
          count: rows.value.length,
          getScrollElement: () => viewport.value ?? null,
          getItemKey: (index: number) => rows.value[index]?.key ?? `absent-${index}`,
          estimateSize: (index: number) => {
            const row = rows.value[index];
            if (row && frozenLayout.value) return frozenLayout.value.height(row);
            return row?.type === "range" ? row.count * estimatedHeight : estimatedHeight;
          },
          measureElement: (node, entry, instance) => {
            const row = rows.value[instance.indexFromElement(node)];
            return row && frozenLayout.value
              ? frozenLayout.value.height(row)
              : measureElement(node, entry, instance);
          },
          scrollToFn: (offset, options, instance) => {
            if (!frozenLayout.value) {
              elementScroll(offset, options, instance);
              programmaticOffset = instance.scrollElement?.scrollTop;
            }
          },
          overscan: 4,
          useAnimationFrameWithResizeObserver: true,
          rangeExtractor: (range: Parameters<typeof defaultRangeExtractor>[0]) =>
            [...new Set([...defaultRangeExtractor(range).slice(0, 64), ...pinned])].sort((
              a,
              b,
            ) => a - b),
        };
      }));
      const items = computed(() => virtual.value.getVirtualItems());
      const size = computed(() => frozenLayout.value?.extent ?? virtual.value.getTotalSize());
      const beginScrollGesture = () => {
        if (frozenLayout.value || signal.aborted || !viewportReady) return;
        navigation++;
        geometryGeneration++;
        following.value = false;
        virtual.value.cancelScroll();
        const measured = new Map(
          virtual.value.takeSnapshot().map((item) => [String(item.key), item.size]),
        );
        frozenLayout.value = new FrozenScrollLayout(
          rows.value,
          measured,
          estimatedHeight,
          viewport.value?.scrollHeight ?? virtual.value.getTotalSize(),
        );
        virtual.value.shouldAdjustScrollPositionOnItemSizeChange = () => false;
        virtual.value.measure();
      };
      const finishScrollGesture = () => {
        heldPointer = undefined;
        heldMouse = false;
        const layout = frozenLayout.value, element = viewport.value;
        if (!layout || !element || signal.aborted) return;
        const atEnd = layout.extent - element.clientHeight - element.scrollTop < 24;
        const item = virtual.value.getVirtualItemForOffset(element.scrollTop);
        const row = item ? rows.value[item.index] : undefined;
        const anchor = item && row ? layout.locate(row, element.scrollTop - item.start) : undefined;
        const generation = ++geometryGeneration, currentNavigation = navigation;
        virtual.value.cancelScroll();
        frozenLayout.value = undefined;
        virtual.value.shouldAdjustScrollPositionOnItemSizeChange = undefined;
        virtual.value.measure();
        following.value = atEnd;
        if (anchor && !atEnd) {
          nextTick().then(() => {
            if (
              signal.aborted || frozenLayout.value || generation !== geometryGeneration ||
              currentNavigation !== navigation
            ) return;
            const index = rows.value.findIndex((row) =>
              row.type === "entry"
                ? row.ordinal === anchor.ordinal
                : row.from <= anchor.ordinal && anchor.ordinal < row.from + row.count
            );
            const target = rows.value[index];
            const position = index >= 0
              ? virtual.value.getOffsetForIndex(index, "start")
              : undefined;
            if (target && position) {
              virtual.value.scrollToOffset(
                position[0] +
                  (target.type === "range" ? (anchor.ordinal - target.from) * estimatedHeight : 0) +
                  anchor.inside,
              );
              emit("anchorRestored", {
                reason: "gesture",
                ordinal: anchor.ordinal,
                offset: anchor.inside,
                ...(target.type === "entry" ? { entryId: target.key } : {}),
              });
            }
          }).catch((error) => {
            if (!signal.aborted) status.value = messages[historyErrorMessageKey(error)];
          });
        }
      };
      const onScrollbar = (event: MouseEvent): boolean => {
        const element = viewport.value;
        if (!element || event.button !== 0 || element.scrollHeight <= element.clientHeight) {
          return false;
        }
        const bounds = element.getBoundingClientRect();
        const gutter = Math.max(18, element.offsetWidth - element.clientWidth);
        return event.clientX >= bounds.right - gutter && event.clientX <= bounds.right &&
          event.clientY >= bounds.top && event.clientY <= bounds.bottom;
      };
      const releasePointer = (event: PointerEvent) => {
        if (event.pointerId === heldPointer) finishScrollGesture();
      };
      const releaseMouse = () => {
        if (heldMouse) finishScrollGesture();
      };
      const scrollEnded = () => {
        if (heldPointer === undefined && !heldMouse) finishScrollGesture();
      };
      watch(items, (items) => {
        if (!signal.aborted) {
          view.setVisible(items.flatMap((item) => {
            const row = rows.value[item.index];
            return row?.type === "entry" ? [row.ordinal] : [];
          }));
          if (viewportReady) requestRangeScan();
        }
      }, { immediate: true });
      const latest = async () => {
        try {
          await runtime.loadPage(undefined, signal);
          await nextTick();
          if (signal.aborted) return;
          following.value = true;
          virtual.value.scrollToOffset(virtual.value.getTotalSize(), { align: "end" });
        } catch (error) {
          if (!signal.aborted) status.value = messages[historyErrorMessageKey(error)];
        }
      };
      watch(size, async () => {
        if (following.value && viewport.value && !frozenLayout.value) {
          await nextTick();
          if (!signal.aborted && !frozenLayout.value) {
            virtual.value.scrollToOffset(virtual.value.getTotalSize(), { align: "end" });
          }
        }
      });
      const loadRange = async (row: Extract<ConversationRow, { type: "range" }>, start: number) => {
        if (signal.aborted) return;
        const ordinal = frozenLayout.value?.locate(
          row,
          Math.max(0, (viewport.value?.scrollTop ?? start) - start),
        ).ordinal ?? Math.min(
          row.from + row.count - 1,
          row.from +
            Math.max(
              0,
              Math.floor(((viewport.value?.scrollTop ?? start) - start) / estimatedHeight),
            ),
        );
        const offset = Math.max(
          row.from,
          Math.floor(ordinal / runtime.limits.pageSize) * runtime.limits.pageSize,
        );
        if (rangeRequest?.key === row.key && rangeRequest.offset === offset) return;
        if (loadErrors.get(row.key)?.offset === offset) return;
        rangeRequest?.controller.abort();
        const request = {
          key: row.key,
          offset,
          controller: new AbortController(),
          progress: { phase: "reading" } as PageLoadProgress,
        };
        rangeRequest = request;
        const requestSignal = AbortSignal.any([signal, request.controller.signal]);
        loadErrors.delete(row.key);
        loadVersion.value++;
        try {
          await runtime.loadPage(offset, requestSignal, (progress) => {
            if (rangeRequest === request && !requestSignal.aborted) {
              request.progress = progress;
              loadVersion.value++;
            }
          });
        } catch (error) {
          if (!requestSignal.aborted) {
            loadErrors.set(row.key, { offset, message: messages[historyErrorMessageKey(error)] });
            while (loadErrors.size > runtime.limits.ranges) {
              const key = loadErrors.keys().next().value;
              if (key !== undefined) loadErrors.delete(key);
            }
            loadVersion.value++;
          }
        } finally {
          if (rangeRequest === request) {
            rangeRequest = undefined;
            loadVersion.value++;
          }
          requestRangeScan();
        }
      };
      const requestRangeScan = () => {
        if (!viewportReady || signal.aborted || scanFrame !== undefined) return;
        scanFrame = requestAnimationFrame(() => {
          scanFrame = undefined;
          const element = viewport.value;
          if (!element || signal.aborted) return;
          const bounds = element.getBoundingClientRect();
          let target: {
            row: Extract<ConversationRow, { type: "range" }>;
            start: number;
            overlap: number;
          } | undefined;
          for (const node of element.querySelectorAll<HTMLElement>("[data-range-key]")) {
            const rect = node.getBoundingClientRect();
            const overlap = Math.min(rect.bottom, bounds.bottom) - Math.max(rect.top, bounds.top);
            if (overlap <= 1 || (target && target.overlap >= overlap)) continue;
            const row = rows.value.find((row) => row.key === node.dataset.rangeKey);
            if (row?.type === "range") {
              target = { row, start: Number(node.dataset.rangeStart), overlap };
            }
          }
          if (target) {
            if (element.scrollHeight - element.clientHeight - element.scrollTop >= 24) {
              following.value = false;
            }
            void loadRange(target.row, target.start);
          }
        });
      };
      const point = (node: Node | null, offset: number): TextPoint | undefined => {
        if (!node) return;
        const element = node instanceof Element ? node : node.parentElement;
        const text = element?.closest<HTMLElement>("[data-text-entry]");
        if (!text || !root.value?.contains(text)) return;
        const id = text.dataset.textEntry;
        if (!id) return;
        const entry = runtime.projection.find(id);
        if (!entry) return;
        const range = document.createRange();
        range.selectNodeContents(text);
        try {
          range.setEnd(node, offset);
        } catch {
          return;
        }
        return {
          id,
          ordinal: entry.ordinal,
          revision: entry.contentRevision,
          offset: range.toString().length,
        };
      };
      const selectionChanged = () => {
        if (signal.aborted) return;
        const selection = document.getSelection();
        if (!selection || selection.isCollapsed) {
          view.setSelection(undefined);
          return;
        }
        const anchor = point(selection.anchorNode, selection.anchorOffset),
          focus = point(selection.focusNode, selection.focusOffset);
        const epoch = runtime.projection.epoch;
        if (anchor && focus && epoch) {
          view.setSelection({
            epoch,
            projectionRevision: runtime.projection.revision,
            anchor,
            focus,
          });
        } else view.setSelection(undefined);
      };
      const copy = async (event: ClipboardEvent) => {
        const selection = document.getSelection();
        if (!selection || !point(selection.anchorNode, selection.anchorOffset) || !view.selection) {
          return;
        }
        event.preventDefault();
        try {
          const text = await view.copySelection();
          signal.throwIfAborted();
          await props.host.writeText(text);
          status.value = messages.copied;
        } catch (error) {
          if (!signal.aborted) status.value = messages[historyErrorMessageKey(error)];
        }
      };
      watch(viewport, async (element) => {
        if (!element || signal.aborted) return;
        document.addEventListener("selectionchange", selectionChanged, { signal });
        const inputLifetime = { capture: true, signal };
        globalThis.addEventListener("pointerup", releasePointer, inputLifetime);
        globalThis.addEventListener("pointercancel", releasePointer, inputLifetime);
        globalThis.addEventListener("mouseup", releaseMouse, inputLifetime);
        globalThis.addEventListener("blur", finishScrollGesture, { signal });
        await nextTick();
        if (signal.aborted) return;
        virtual.value.scrollToOffset(virtual.value.getTotalSize(), { align: "end" });
        lastScrollTop = viewport.value?.scrollTop ?? 0;
        viewportReady = true;
        requestRangeScan();
        emit("ready");
      }, { flush: "post", once: true });
      onScopeDispose(() => {
        lifetime.abort(new DOMException("History unmounted", "AbortError"));
        document.removeEventListener("selectionchange", selectionChanged);
        globalThis.removeEventListener("pointerup", releasePointer, true);
        globalThis.removeEventListener("pointercancel", releasePointer, true);
        globalThis.removeEventListener("mouseup", releaseMouse, true);
        globalThis.removeEventListener("blur", finishScrollGesture);
        signal.removeEventListener("abort", close);
        if (scanFrame !== undefined) cancelAnimationFrame(scanFrame);
        rangeRequest?.controller.abort();
        signal.removeEventListener("abort", stopSubscriptions);
        stopSubscriptions();
        if (!view.signal.aborted) {
          view.setVisible([]);
          view.setSelection(undefined);
        }
      });
      return () =>
        h("section", {
          key,
          class: "mui-history",
          "aria-label": props.title || props.localize(messages.history),
          ref: root,
          "data-history-view-id": view.id,
          onCopy: copy,
          onFocusin: (event: FocusEvent) => {
            const target = event.target;
            const article = target instanceof Element
              ? target.closest<HTMLElement>("[data-ordinal]")
              : null;
            focused.value = article ? Number(article.dataset.ordinal) : undefined;
          },
          onFocusout: (event: FocusEvent) => {
            const target = event.relatedTarget;
            if (
              !(target instanceof Element) || !root.value?.contains(target) ||
              !target.closest("[data-ordinal]")
            ) focused.value = undefined;
          },
        }, [
          h(
            "h2",
            { class: "mui-history-heading" },
            props.title || props.localize(messages.history),
          ),
          h("div", {
            class: "mui-viewport",
            ref: viewport,
            role: "region",
            "aria-label": props.localize(messages.history),
            "aria-live": "off",
            tabindex: 0,
            "data-scroll-gesture": frozenLayout.value ? "active" : "idle",
            onPointerdown: (event: PointerEvent) => {
              if (onScrollbar(event)) {
                heldPointer = event.pointerId;
                beginScrollGesture();
              }
            },
            onMousedown: (event: MouseEvent) => {
              if (onScrollbar(event)) {
                heldMouse = true;
                beginScrollGesture();
              }
            },
            onScrollend: scrollEnded,
            onWheel: () => {
              navigation++;
              following.value = false;
            },
            onTouchstart: () => {
              navigation++;
              following.value = false;
            },
            onKeydown: (event: KeyboardEvent) => {
              if (
                ["ArrowUp", "PageUp", "Home", "ArrowDown", "PageDown", "End"].includes(event.key)
              ) {
                navigation++;
                following.value = false;
              }
            },
            onScroll: () => {
              const element = viewport.value;
              if (!element) return;
              const fromApplication = programmaticOffset !== undefined &&
                Math.abs(element.scrollTop - programmaticOffset) < 1;
              programmaticOffset = undefined;
              // Native scrollbar controls may consume pointer events; scroll/scrollend is the fallback.
              if (!fromApplication && Math.abs(element.scrollTop - lastScrollTop) > 1) {
                beginScrollGesture();
              }
              const atEnd = element.scrollHeight - element.clientHeight - element.scrollTop < 24;
              if (atEnd && !frozenLayout.value) following.value = true;
              else if (element.scrollTop < lastScrollTop - 1) following.value = false;
              if (!following.value && Math.abs(element.scrollTop - lastScrollTop) > 1) navigation++;
              lastScrollTop = element.scrollTop;
              requestRangeScan();
            },
          }, [
            h(
              "ol",
              {
                class: "mui-history-space",
                role: "list",
                "aria-label": props.localize(messages.entries),
                style: {
                  "--mui-virtual-extent": `${size.value}px`,
                },
              },
              items.value.map((item) => {
                const row = rows.value[item.index];
                if (!row) return null;
                if (row.type === "range") {
                  void loadVersion.value;
                  const request = rangeRequest?.key === row.key ? rangeRequest : undefined;
                  const phase = request?.progress.phase;
                  const loadError = loadErrors.get(row.key);
                  return h("li", {
                    key: row.key,
                    class: "mui-range",
                    "data-range-key": row.key,
                    "data-range-start": item.start,
                    "data-range-state": phase ?? (loadError ? "failed" : "idle"),
                    "aria-busy": !!request,
                    "aria-posinset": row.from + 1,
                    "aria-setsize": runtime.projection.total,
                    "aria-label": props.localize(
                      messages.unloadedRange,
                      row.from + 1,
                      row.from + row.count,
                    ),
                    style: {
                      "--mui-virtual-size": `${item.size}px`,
                      "--mui-virtual-offset": `${item.start}px`,
                    },
                    onVnodeMounted: requestRangeScan,
                    onVnodeUpdated: requestRangeScan,
                  }, [
                    h("div", { class: "mui-range-label" }, [
                      loadError
                        ? props.localize(loadError.message)
                        : (phase === "waiting-for-stream"
                          ? props.localize(messages.synchronizing)
                          : request
                          ? props.localize(messages.loadingHistory)
                          : props.localize(messages.loadOnRead)),
                      h(
                        "button",
                        {
                          class: "mui-button mui-range-load",
                          type: "button",
                          onClick: () => {
                            if (rangeRequest?.key === row.key) {
                              rangeRequest.controller.abort();
                              rangeRequest = undefined;
                            }
                            loadErrors.delete(row.key);
                            following.value = false;
                            void loadRange(row, item.start);
                          },
                        },
                        request
                          ? props.localize(messages.cancelRetry)
                          : props.localize(messages.loadRange),
                      ),
                    ]),
                  ]);
                }
                const entry = runtime.projection.get(row.ordinal);
                const joinsTool = (ordinal: number) => {
                  if (entry?.kind !== "tool") return false;
                  const neighbor = runtime.projection.get(ordinal);
                  return neighbor?.kind === "tool" &&
                    neighbor.interactionId === entry.interactionId;
                };
                return h("li", {
                  key: row.key,
                  class: "mui-slot",
                  "aria-posinset": row.ordinal + 1,
                  "aria-setsize": runtime.projection.total,
                  style: {
                    "--mui-virtual-size": `${item.size}px`,
                    "--mui-virtual-offset": `${item.start}px`,
                  },
                }, [h(OutputBlockHost, {
                  runtime,
                  view,
                  host: props.host,
                  localize: props.localize,
                  entryId: row.key,
                  index: item.index,
                  joinsPreviousTool: joinsTool(row.ordinal - 1),
                  joinsNextTool: joinsTool(row.ordinal + 1),
                  // The article measures its natural height; the slot clips pending growth.
                  measure: (element: HTMLElement) => virtual.value.measureElement(element),
                })]);
              }),
            ),
          ]),
          !following.value
            ? h(
              "button",
              { class: "mui-button mui-latest", type: "button", onClick: latest },
              props.localize(messages.latest),
            )
            : null,
          h(
            "p",
            { class: "mui-status", role: "status", "aria-live": "polite" },
            status.value ? props.localize(status.value) : "",
          ),
        ]);
    });
  },
});

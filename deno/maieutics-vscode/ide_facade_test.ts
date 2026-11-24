/// <reference lib="deno.ns" />
import { assert, assertEquals } from "@std/assert";
import { testIdeFacade } from "../test/ide_facade_contract.ts";
import {
  createIde,
  IdeFolderRegistry,
  IdeUnavailableError,
  isValidIdeFolderId,
  isValidIdeFsSegment,
  shiftPositions,
} from "./ide.ts";

testIdeFacade("VS Code facade", { createIde, IdeUnavailableError, shiftPositions });

Deno.test("IdeFolderRegistry binds ids for life and never reuses them", () => {
  const registry = new IdeFolderRegistry("abc123xy");
  const first = registry.register("file:///one");
  const second = registry.register("file:///two");
  assert(first.startsWith("abc123xy"), "ids carry the activation nonce");
  assert(first !== second, "distinct folders get distinct ids");
  assertEquals(registry.resolve(first), "file:///one");
  assertEquals(registry.resolve("zz9"), undefined, "foreign ids resolve to nothing");
  assert(isValidIdeFolderId(first), "the id matches the token shape");
  assert(!isValidIdeFsSegment(".."), "dot-dot segments are rejected");
});

/**
 * Client-side cache of the kernel's live skill catalog (`GET /v1/skills`),
 * kept free of the `vscode` module so it is unit-testable under Deno. Cache
 * policy for stage 1 is fetch-once-per-session plus explicit refresh; the
 * `skills.catalog` event frame supersedes the manual refresh when it lands.
 */

export interface SkillInfo {
  readonly name: string;
  readonly description: string;
  readonly source: string;
}

export class SkillsCatalog {
  #skills: SkillInfo[] | null = null;
  readonly #fetchSkills: () => Promise<SkillInfo[]>;

  constructor(fetchSkills: () => Promise<SkillInfo[]>) {
    this.#fetchSkills = fetchSkills;
  }

  /** The catalog, fetched on first use and cached; every failure degrades to
   * "no skills offered" (completion just never fires) with the error rethrown
   * on the next call, never cached. */
  async all(): Promise<SkillInfo[]> {
    if (this.#skills === null) {
      try {
        this.#skills = await this.#fetchSkills();
      } catch {
        return [];
      }
    }
    return this.#skills;
  }

  /** Forces the next {@linkcode all} call back to the wire. */
  invalidate(): void {
    this.#skills = null;
  }

  /** The skills whose names start with the typed prefix (the `$` stripped by
   * the caller). Empty prefix returns everything. */
  query(prefix: string): SkillInfo[] {
    return this.#skills === null
      ? []
      : this.#skills.filter((skill) => skill.name.startsWith(prefix));
  }
}

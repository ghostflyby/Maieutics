/**
 * Test fixture: a target module whose top-level evaluation fails. Verifies
 * that a bootstrap/target failure surfaces as Worker startup failure (the
 * wrapper's import rejection) and that the actor owner observes the death.
 */

throw new Error("fixture target top-level failure");

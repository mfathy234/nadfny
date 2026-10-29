// Pure classification of worktrees, shared by the Worktrees view and the Overview recommendations.
// Unknown always counts as risky: a worktree is "safe" only when git positively confirmed it.
import { daysSince } from '../format.js';

export const STALE_DAYS = 30;

/** Uncommitted changes, commits that exist nowhere else, a lock, or a state git could not confirm. */
export const isRisky = (w) => !w.missing && (
  !w.statusKnown || w.locked || w.changedFiles > 0 || w.ahead > 0 || (!w.hasUpstream && !w.merged));

/** Merged into the default branch with a verified clean tree, or its folder is truly gone. */
export const isSafe = (w) => (w.missing && !w.driveOffline)
  || (!w.missing && w.statusKnown && w.merged && w.changedFiles === 0 && w.ahead === 0 && !w.locked);

export const isStale = (w) => !w.missing && daysSince(w.lastCommitUtc ?? w.lastWriteUtc) >= STALE_DAYS;

export const FILTERS = {
  all: () => true,
  safe: isSafe,
  stale: isStale,
  risky: isRisky,
};

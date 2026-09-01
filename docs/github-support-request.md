# GitHub Support request — purge unreachable objects after a history rewrite

Submit at <https://support.github.com/request> (signed in as AegiosOT).
Category: *Account or repository* → *Data removal / sensitive data*.

Delete this file once GitHub confirms the objects are gone.

---

**Subject:** Remove unreachable objects after history rewrite — AegiosOT/YTile

**Body:**

Hello,

I rewrote the history of my repository **AegiosOT/YTile** to remove personal
information (a legal name that appeared in the `LICENSE` file) and
force-pushed the corrected history to `main`. The affected commits are no
longer reachable from any branch or tag, but they are still served by
GitHub when addressed by their SHA, so the data remains publicly retrievable.

Could you please run garbage collection on the repository to permanently
remove the unreachable objects?

Unreachable commits that still resolve:

- `90554db412e26a1b3b20cd45b1ab2c350745621a`
- `1d5ce052d757305d9d48a6f01b1861c8aa4848cf`
- `d2dc2d103e6a6337f169bb8065e3fbe51872bafe`
- `5e238fba0c2d3264d8d8f471feac943d8eca327a`
- `83316d1` (and the remaining commits of that rewritten range)

The blob holding the personal information:

- `2b95ddbdd3e274bd90353484e31966fd3d74d5b4`

The repository has **no forks**, and the pull requests that referenced it
have been closed, so I do not believe the objects are retained anywhere else
— but please let me know if you find any other copies that need removing.

Thank you.

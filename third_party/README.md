# third_party/

**Purpose.** This directory is reserved for source code imported from external GPL-compatible projects — primarily MacroQuest (MQ2, GPLv2-only) — when EQSwitch needs to borrow from them directly rather than reach across the process/SHM boundary.

As of 2026-04-15 this directory is empty. It exists so that the *first* commit introducing a derived file has a well-defined home and a clear convention to follow, rather than scattering attribution into ad-hoc paths.

## Rules for importing code here

1. **Keep the upstream copyright + license header verbatim** at the top of every imported file. Do not replace it with EQSwitch's SPDX header. GPLv2 §2 requires preserving the original notice.

2. **If you modify an imported file, add a second, clearly-marked block** below the upstream header documenting what changed. Example:
   ```cpp
   /*
    * MacroQuest: The extension platform for EverQuest
    * Copyright (C) 2002-present MacroQuest Authors
    * Licensed under GNU GPL v2 — see LICENSE in the MacroQuest project.
    */

   /*
    * Modifications for EQSwitch by itsnateai, 2026.
    * - Stripped XXX subsystem (unused outside MQ2 plugin context)
    * - Replaced YYY with ZZZ to match EQSwitch SHM protocol
    * Licensed under GPL-2.0-or-later (compatible with upstream GPLv2-only).
    */
   ```

3. **Put the SPDX identifier on the modifications block, not the upstream header.** The file as a whole is now dual-traced: upstream GPLv2 + EQSwitch modifications GPL-2.0-or-later. These are compatible because EQSwitch is GPL-2.0-**or-later**, which can accept GPLv2-only inbound.

4. **Organize by upstream project** — e.g., `third_party/macroquest/GiveTime.cpp`, `third_party/macroquest/eqlib_offsets.h`. Don't flatten into `third_party/` directly.

5. **Record the upstream provenance** in a sibling `NOTICE.md` — which MQ2 commit / tag the file came from, date imported, any upstream bug fixes since that you might want to pull forward.

6. **Check `LICENSE` + `README.md` attribution section** after every first import of a new upstream project, and add an entry mentioning the directory path and license.

## Why this exists before any files do

Set-up cost before the fact is trivial. Figuring out the right convention while under pressure to ship v7 is not. If you're reading this while about to commit the first MQ2-derived file, follow the rules above and add a one-line entry at the top of this README recording what you imported.

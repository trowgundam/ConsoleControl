---
name: navigate-nintendo-switch
description: Navigate Nintendo Switch and Switch 2 system menus with ConsoleControl digital inputs. Use when opening HOME, finding software outside the visible row, navigating System Settings, changing controller screens, or recovering from an uncertain menu state.
---

# Navigate Nintendo Switch

Use this skill with `operate-console-control`. Read [the researched menu reference](../../../docs/research/switch-menu-navigation.md) when the task involves library, settings, controller, or recovery details.

## Use the common controls

- Press `home` to open the HOME Menu from most software.
- Use `dpad_up`, `dpad_right`, `dpad_down`, and `dpad_left` to move focus.
- Press `a` to choose the focused item.
- Press `b` to return or cancel.
- Use `plus` or `minus` only when the visible screen labels that action.

Take a low-fidelity screenshot before navigating. After each input or short sequence, take another screenshot and identify the focus marker before continuing. Nintendo menus can animate or ignore input while loading, so prefer verified small steps over a long blind sequence.

## Return to a known state

Press `home` once, pause briefly, and capture a screenshot. If HOME opens Quick Settings instead, the button was likely held too long; release control if necessary, reacquire it, and use a short press. Use `b` to dismiss modal dialogs before trying to move around the HOME Menu.

## Find software

On Switch 2, move to the far-right `Show More` item on the HOME Menu and press `a`. Filter between all and downloaded software if the visible screen offers that choice. Sort by recent use, total play time, title, or publisher when useful.

On original Switch, `All Software` appears at the far right only after the system has at least 13 software icons. In All Software, use `right_shoulder` to change sorting. On system version 14.0.0 or later, use `left_shoulder` to open Groups and `plus` to create a group when those hints are visible.

Nintendo's public instructions do not document a text search in these library screens. Do not claim one exists without visible evidence.

## Open System Settings

From HOME, move along the bottom function row to the gear icon and press `a`. Navigate the category list vertically, press `a` or `dpad_right` to enter a category, and use `b` to move back. Switch and Switch 2 category names differ; inspect the screenshot rather than relying on a memorized number of D-pad presses.

## Handle uncertainty

If the selected item is unclear, render the same screenshot at medium fidelity before taking another screenshot. If it remains unclear, render high. Before confirming a dialog, inspect it and identify both the focused action and its consequence. When an unexpected dialog appears, stop the planned sequence and tell the user before accepting destructive, account, purchase, transfer, initialization, or recovery actions.

After closing software, capture a screenshot and confirm that its `Playing` indicator is gone before reporting that the software closed.

Never automate purchases, deletion, factory initialization, account changes, parental-control changes, data transfer, or recovery-mode actions without explicit user direction for that exact action.

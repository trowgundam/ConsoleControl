# Nintendo Switch system-menu navigation

This note records menu behavior that an automation agent can rely on. It uses only first-party Nintendo support pages. Menu labels are the current English labels in those pages as of 2026-08-28.

## Common controller behavior

- Press **HOME** to open the HOME Menu from software. Nintendo says the system menus remain available while software runs and let the user return to that software afterward. [Nintendo Switch system-menu overview](https://en-americas-support.nintendo.com/app/answers/detail/a_id/22524/~/system-menu-overview) and [Nintendo Switch 2 HOME Menu overview](https://www.nintendo.com/au/support/articles/nintendo-switch-2-home-menu-overview/)
- Use the left stick or directional buttons to move focus, then press **A** to select. Nintendo documents this control pattern in its Switch 2 accessibility and Mii instructions. [Switch 2 accessibility guide](https://en-americas-support.nintendo.com/app/answers/detail/a_id/68395/) and [create or edit a Mii on Switch 2](https://en-americas-support.nintendo.com/app/answers/detail/a_id/68302/)
- Press **B** to return to the previous screen. Nintendo documents **B** as the return control in menu procedures on both systems. [Add a user on Nintendo Switch](https://www.nintendo.com/au/support/articles/how-to-add-a-new-user-account-on-nintendo-switch/) and [test the touch screen on Switch 2](https://www.nintendo.com/au/support/articles/how-to-run-the-touch-screen-test-on-nintendo-switch-2/)
- Press **+** or **-** while a software icon has focus to open that title's options without starting it. Nintendo documents this route for manual software updates on both systems. [Update software on Nintendo Switch](https://www.nintendo.com/au/support/articles/how-to-update-software/) and [update software on Switch 2](https://www.nintendo.com/au/support/articles/how-to-update-software-on-nintendo-switch-2/)

These controls can be remapped. On Nintendo Switch, **System Settings > Controllers and Sensors > Change Button Mapping** can remap most controls on supported Nintendo controllers. [Nintendo Switch button-mapping instructions](https://en-americas-support.nintendo.com/app/answers/detail/a_id/49229/)

## Return to a known screen

Use these recovery actions in order:

1. Press **B** to dismiss a dialog or return one screen.
2. Press **HOME** once to return to the HOME Menu from software or another system menu. Nintendo explicitly supports **HOME** as an exit from Quick Settings and the Power Menu. [Nintendo Switch Quick Settings](https://en-americas-support.nintendo.com/app/answers/detail/a_id/22321/) and [Switch 2 Power Menu](https://www.nintendo.com/au/support/articles/how-to-access-the-power-menu-turn-off-restart-sleep-mode-on-a-nintendo-switch-2-console/)
3. If the console does not respond normally, hold the physical **POWER** button for at least three seconds, select **Power Options**, then select **Restart**. Do not use this as ordinary navigation. [Restart Nintendo Switch](https://www.nintendo.com/au/support/articles/error-message-you-must-have-the-software-in-order-to-play/) and [Switch 2 Power Menu](https://www.nintendo.com/au/support/articles/how-to-access-the-power-menu-turn-off-restart-sleep-mode-on-a-nintendo-switch-2-console/)

Pressing **HOME** is the safest automation reset because it does not select a destructive option. The HOME Menu may preserve the last focus position, so inspect the screen before sending directional input.

## HOME Menu layout

The HOME Menu contains a horizontal software row and system destinations. On Nintendo Switch 2, Nintendo lists the user page, software icons, controller status, Nintendo Switch Online, GameChat, News, Nintendo eShop, Album, GameShare, Controllers, Virtual Game Cards, System Settings, and Sleep Mode. [Switch 2 HOME Menu guide](https://support.nintendo.com/sg/switch2/play/use/homemenu/index.html)

Nintendo Switch has Nintendo Switch Online, News, Nintendo eShop, Album, GameShare, Controllers, Virtual Game Cards, System Settings, and Sleep Mode along the bottom. Switch 2 adds GameChat and has its own GameShare hosting support. [Nintendo Switch HOME Menu overview](https://www.nintendo.com/en-gb/Support/Nintendo-Switch/Nintendo-Switch-HOME-Menu-Overview-1406405.html) and [Switch 2 HOME Menu guide](https://support.nintendo.com/sg/switch2/play/use/homemenu/index.html)

Useful destinations include:

- Select a software icon to start it. [Switch 2 HOME Menu guide](https://support.nintendo.com/sg/switch2/play/use/homemenu/index.html)
- Select the user icon at the upper left to manage the profile, play activity, and friends. [Switch 2 HOME Menu guide](https://support.nintendo.com/sg/switch2/play/use/homemenu/index.html)
- Select **Controllers** to pair controllers or change their order. The controller screen also reports the approximate battery level of each paired Nintendo Switch controller. [Switch 2 HOME Menu guide](https://support.nintendo.com/sg/switch2/play/use/homemenu/index.html) and [Nintendo Switch battery guide](https://en-americas-support.nintendo.com/app/answers/detail/a_id/22363/)
- Select **System Settings** to configure the console. [Nintendo Switch System Settings instructions](https://en-americas-support.nintendo.com/app/answers/detail/a_id/22322/)
- Select **Sleep Mode** at the right end of the system destinations to put the console to sleep. Switch 2 uses the same route. [Put Switch 2 into Sleep Mode](https://www.nintendo.com/au/support/articles/how-to-put-the-nintendo-switch-2-into-sleep-mode/)

## Find software that is not on the HOME Menu

### Nintendo Switch

**All Software** appears only when the console has at least 13 software icons. Scroll all the way right on the HOME Menu and select **All Software**. [Organize software on Nintendo Switch](https://en-americas-support.nintendo.com/app/answers/detail/a_id/44645/)

Within **All Software**:

- Press **R** to sort by time last played, total play time, title, or developer. Sorting this view does not rearrange the HOME Menu. [Organize software on Nintendo Switch](https://en-americas-support.nintendo.com/app/answers/detail/a_id/44645/)
- On system version 14.0.0 or later, press **L** to view groups. The first group starts with **Create New Group**. After a group exists, press **+** to create another. A console can have up to 100 groups, each with up to 200 titles, and one title can belong to multiple groups. [Create groups on Nintendo Switch](https://en-americas-support.nintendo.com/app/answers/detail/a_id/58092/)

Nintendo's current first-party instructions do not document a text-search control or a downloaded-only filter in **All Software**. An agent should use sorting or groups and inspect the displayed titles.

### Nintendo Switch 2

Scroll all the way right on the HOME Menu and select **Show More**. [Organize software on Switch 2](https://en-americas-support.nintendo.com/app/answers/detail/a_id/68326/)

Within **Show More**:

- Select the filter icon on the left to show all software or only downloaded software.
- Select the reorder icon to sort by recently played, longest play time, shortest play time, title, or publisher.
- These options do not rearrange HOME Menu icons.

Nintendo documents all three behaviors on its [Switch 2 software-organization page](https://en-americas-support.nintendo.com/app/answers/detail/a_id/68326/). That page does not document groups or a text-search control on Switch 2.

Switch 2 also has **Virtual Game Cards** on the HOME Menu. Purchased digital software and downloadable content appear there. Loading a virtual game card makes its software icon appear on the HOME Menu. [Switch 2 HOME Menu guide](https://support.nintendo.com/sg/switch2/play/use/homemenu/index.html)

## Navigate System Settings

From the HOME Menu, select **System Settings**. Move through the category list on the left, select a category, then move through its options on the right. [Nintendo Switch System Settings instructions](https://en-americas-support.nintendo.com/app/answers/detail/a_id/22322/)

### Nintendo Switch categories

Nintendo documents these categories for the original Switch family:

- Support/Health & Safety
- Airplane Mode
- Screen Brightness
- Bluetooth Audio
- Screen Lock
- Parental Controls
- Internet
- Data Management
- Users
- Mii
- amiibo
- Themes
- Notifications
- Sleep Mode
- Controllers and Sensors
- TV Settings on dockable models
- System

The exact options vary by Nintendo Switch, Nintendo Switch OLED Model, and Nintendo Switch Lite. Nintendo's [Nintendo Switch System Settings overview](https://en-americas-support.nintendo.com/app/answers/detail/a_id/22526/) lists the model-specific options. Common routes include **Internet** for connection setup, **Data Management** for software and save-data management, **Users** for user management, **Controllers and Sensors** for pairing and calibration, and **System** for updates, language, region, date, battery display, and initialization options.

### Nintendo Switch 2 categories

Nintendo documents these Switch 2 categories:

- Support/Health & Safety
- Flight Mode in Nintendo's Australian English documentation, called Airplane Mode in its American documentation
- Screen Brightness
- Bluetooth Audio
- Internet
- Parental Controls
- Accessibility
- Data Management
- Users
- Controllers & Accessories
- Audio
- Display
- Mii
- amiibo
- Themes
- Notifications
- Sleep Mode
- System

Switch 2 moves button mapping and text-to-speech into **Accessibility**, uses **Controllers & Accessories** for controller and camera settings, and splits audio and display settings into their own categories. It also adds Switch 2-specific display options such as 4K, 120 Hz, HDR, and ALLM. The full category and option list is in Nintendo's [Switch 2 System Settings overview](https://www.nintendo.com/au/support/articles/nintendo-switch-2-system-settings-overview/).

For a stable path to the system version on either console, select **System Settings**, scroll to the bottom of the left category list, select **System**, and read **System Update**. [Switch 2 system-version instructions](https://www.nintendo.com/au/support/articles/how-to-determine-the-system-menu-version-on-nintendo-switch-2/) and [Nintendo Switch update history](https://en-americas-support.nintendo.com/app/answers/detail/a_id/43314/)

## Quick Settings, power, and controllers

Hold **HOME** for at least one second to open Quick Settings on either console. Quick Settings appears on the right side. Press **B** or **HOME** to close it. Both systems expose Sleep Mode, automatic brightness, manual brightness, volume, and airplane mode there. [Nintendo Switch Quick Settings](https://en-americas-support.nintendo.com/app/answers/detail/a_id/22321/) and [Switch 2 Quick Settings](https://en-americas-support.nintendo.com/app/answers/detail/a_id/68185)

Switch 2 can also show GL/GR assignment and button-mapping entries in Quick Settings when their requirements are met. Nintendo saves GL/GR assignments per game and per user. [Switch 2 Quick Settings](https://en-americas-support.nintendo.com/app/answers/detail/a_id/68185)

Hold the physical **POWER** button for at least three seconds to open the Power Menu. Select **Power Options**, then **Sleep Mode**, **Restart**, or **Turn Off**. Press **B** or **HOME** to exit without choosing a power action. On Switch 2, ten seconds of inactivity while the Power Menu is open puts the console into Sleep Mode. [Switch 2 Power Menu](https://www.nintendo.com/au/support/articles/how-to-access-the-power-menu-turn-off-restart-sleep-mode-on-a-nintendo-switch-2-console/)

Airplane mode disables wireless communication by default. If detached Joy-Con or Joy-Con 2 stop working after airplane mode is enabled, touchscreen input may be required to re-enable Bluetooth. On Nintendo Switch, the route is **System Settings > Airplane Mode > Controller Connection (Bluetooth)**. On Switch 2, the route is **System Settings > Airplane Mode > Bluetooth**. [Nintendo Switch Quick Settings](https://en-americas-support.nintendo.com/app/answers/detail/a_id/22321/) and [Switch 2 Quick Settings](https://en-americas-support.nintendo.com/app/answers/detail/a_id/68185)

Nintendo Switch controllers can work with compatible Switch 2 software, but their **HOME** button cannot wake a Switch 2 from Sleep Mode. Use the Switch 2 **POWER** button or a Switch 2 controller's **HOME** button for wake-up automation. [Nintendo Switch to Switch 2 transfer guide](https://www.nintendo.com/us/gaming-systems/switch-2/transfer-guide/)

## Automation cautions

- Inspect a screenshot before each directional sequence. Focus can remain on a prior item, software rows vary by installed titles, and System Settings can open at a previously visited category.
- Prefer label recognition to fixed movement counts. Nintendo has added menu destinations through system updates, and Switch 2 has extra HOME Menu destinations.
- Treat **Initialize Console**, software deletion, user deletion, unlinking, system transfer, and power actions as destructive or disruptive. Do not select them during exploratory navigation.
- Do not enable airplane mode during remote controller automation unless touchscreen control or another recovery path is available.
- Use **HOME** to recover from software, **B** to back out of menus, and another screenshot to confirm the resulting screen before continuing.

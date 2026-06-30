# Fix Buttons

In settings, there are "Browse" and "Close" buttons. They look different than other buttons in the interface (e.g., text size and text vertical alignment).

Make a central component to handle all buttons. Call it something like CommonButton (it should allow for minor deviations, such as primary/secondary or whatever naming you come up with). If there are other specialty buttons in the UI (e.g., the stages look like they might be buttons, and the task list might have buttons, and the activity tabs might be buttons), then those specialty buttons should each get their own component type if they aren't already one.

There should be a central place with all the custom button components. There should be an automated test that prevents normal buttons from appearing anywhere except this central place for the buttons.

A non-exhaustive list of things to consider for common buttons:

- Account for the grey buttons (e.g., "Cancel"), the blue buttons (e.g., "Run All" and "Create Task"), and the yellow buttons (e.g., "Pause After Task").
- Account for the various button states.
- Decide what to do with buttons like "Settings" that seem to have some sort of glyph within the button.


Decide what to do with the buttons that have no text (e.g., section expand, collapse, and status). Those seem to have special styling, so maybe they're another special button component or two.
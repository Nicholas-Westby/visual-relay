# Use Component Composition for Buttons

Right now, the button components (CommonButton, IconButton, StageCardButton) all inherit from the Button class. We should never inherit from Button, as we don't want all the extra behaviors buttons support. We should instead make sure all 3 of these custom button components are composed of buttons within them.

This means, among aother things, modifying NoClassInheritsFromButton so it doesn't grandfather in these 3 classes (no class should inherit from Button).
# Use Composition Over Inheritance for Custom Buttons

Recently, we had a task to create custom buttons and avoid the use of any plain button components. The intention was to avoid odd custom variations.

However, there are still odd custom variations, such as the "Save" buttons next to the API keys in the settings screen.

One of the reasons for this is that the implementation inherits from buttons rather than creating new components that are composed of buttons.

To address this, we should make the button components be composed of buttons rather than inheriting from buttons.

We should also add a test that fails if a class is detected that inherits from button.

This would allow us to restrict the customizations allowed for a given button type so that only intentional variations are allowed and those variations are managed via fewer files.
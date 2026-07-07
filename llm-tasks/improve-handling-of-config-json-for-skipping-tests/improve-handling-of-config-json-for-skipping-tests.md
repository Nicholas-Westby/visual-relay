# Improve Handling of config.json For Skipping Tests

I marked a test as "Skip automated testing" and that updated the .relay/config.json file for the skipTestsTaskIds section. However, I noticed a completed/archived task was still listed in that array.

Once a task is completed/archived, there is no reason to mark it as needing to skip tests, and that should be reflected in the config.json file.
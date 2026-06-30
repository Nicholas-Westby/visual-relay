# Recognize New Tasks for "Run All"

When someone clicks "Run All", all of the tasks are indeed processed. However, that is just all the tasks that existed at the time that button was clicked.

If new tasks are created while a run is happening, those new tasks are not included in the run. They ought to be. However, to give the person time to make edits to new tasks (such as including attachments), those new tasks shouldn't be touched until the current batch is fully processed. That is especially important with a "Standard" run all given that allows for parallel processing of tasks. For the "Sequential" run all, it would naturally wait for the existing tasks to be processed before the new one is processed.

If someone decides to reorder a new task during a "Sequential" run all, that task should be processed in the chosen order rather than waiting for the full existing batch to complete.
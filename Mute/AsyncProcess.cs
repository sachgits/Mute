﻿using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;

namespace Mute
{
    public static class AsyncProcess
    {
        public static async Task<int> StartProcess([NotNull] string filename, [NotNull] string arguments, [NotNull] string workingDirectory, int? timeout = null)
        {
            var startInfo = new ProcessStartInfo {
                CreateNoWindow = true,
                Arguments = arguments,
                FileName = filename,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.Start();
                var cancellationTokenSource = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : new CancellationTokenSource();

                await process.WaitForExitAsync(cancellationTokenSource.Token);

                return process.ExitCode;
            }
        }

        /// <summary>
        /// Waits asynchronously for the process to exit.
        /// </summary>
        /// <param name="process">The process to wait for cancellation.</param>
        /// <param name="cancellationToken">A cancellation token. If invoked, the task will return
        /// immediately as cancelled.</param>
        /// <returns>A Task representing waiting for the process to end.</returns>
        private static Task WaitForExitAsync([NotNull] this Process process, CancellationToken cancellationToken = default(CancellationToken))
        {
            process.EnableRaisingEvents = true;

            var taskCompletionSource = new TaskCompletionSource<object>();

            void Handler(object sender, EventArgs args)
            {
                process.Exited -= Handler;
                taskCompletionSource.TrySetResult(null);
            }

            process.Exited += Handler;

            if (cancellationToken != default(CancellationToken))
            {
                cancellationToken.Register(
                    () => {
                        process.Exited -= Handler;
                        taskCompletionSource.TrySetCanceled();
                    });
            }

            return taskCompletionSource.Task;
        }
    }
}
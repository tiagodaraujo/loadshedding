using System;
using System.Threading;
using System.Threading.Tasks;
using Farfetch.LoadShedding.Constants;
using Farfetch.LoadShedding.Events;
using Farfetch.LoadShedding.Events.Args;
using Farfetch.LoadShedding.Exceptions;

namespace Farfetch.LoadShedding.Tasks
{
    internal class TaskManager : ITaskManager
    {
        private readonly TaskQueue _taskQueue;

        private readonly ConcurrentCounter _counter = new ConcurrentCounter();

        private readonly int _queueTimeout;
        private readonly ILoadSheddingEvents _events;

        public TaskManager(
            int concurrencyLimit,
            int maxQueueSize,
            int queueTimeout = Timeout.Infinite,
            ILoadSheddingEvents events = null)
        {
            this._counter.Limit = concurrencyLimit;
            this._queueTimeout = queueTimeout;

            this._events = events;

            this._taskQueue = new TaskQueue(maxQueueSize);

            this._events?.ConcurrencyLimitChanged?.Raise(new LimitChangedEventArgs(this._counter.Limit));
            this._events?.QueueLimitChanged?.Raise(new LimitChangedEventArgs(maxQueueSize));
        }

        public int ConcurrencyLimit
        {
            get => this._counter.Limit;
            set
            {
                if (value < 1)
                {
                    throw new ArgumentException("Must be greater than or equal to 1.", nameof(this.ConcurrencyLimit));
                }

                if (value == this._counter.Limit)
                {
                    return;
                }

                this._counter.Limit = value;

                this.NotifyConcurrencyLimitChanged();
                this.ProcessPendingTasks();
            }
        }

        public int QueueLimit
        {
            get => this._taskQueue.Limit;
            set
            {
                if (value == this._taskQueue.Limit)
                {
                    return;
                }

                this._taskQueue.Limit = value;

                this._events?.QueueLimitChanged?.Raise(new LimitChangedEventArgs(this._taskQueue.Limit));
            }
        }

        public int QueueCount => this._taskQueue.Count;

        public int ConcurrencyCount => this._counter.Count;

        public double UsagePercentage => this._counter.UsagePercentage;

        public async Task<TaskItem> AcquireAsync(Priority priority, CancellationToken cancellationToken = default)
        {
            var item = this.CreateTask(priority);

            if (this._counter.TryIncrement(out var currentCount))
            {
                item.Process();

                this.NotifyItemProcessing(item, currentCount);

                return item;
            }

            this._taskQueue.Enqueue(item);

            if (item.Status != TaskResult.Rejected)
            {
                try
                {
                    this.NotifyItemEnqueued(this._taskQueue.Count, item);

                    await item
                        .WaitAsync(this._queueTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    this._taskQueue.Remove(item);
                    this.NotifyItemDequeued(this._taskQueue.Count, item);
                }
            }

            switch (item.Status)
            {
                case TaskResult.Rejected:
                    this.NotifyItemRejected(item, RejectionReasons.MaxQueueItems);
                    throw new QueueLimitReachedException(this._taskQueue.Limit);
                case TaskResult.Timeout:
                    cancellationToken.ThrowIfCancellationRequested();
                    this.NotifyItemRejected(item, RejectionReasons.QueueTimeout);
                    throw new QueueTimeoutException(this._queueTimeout);
                default:
                    break;
            }

            this.NotifyItemProcessing(item, this._counter.Increment());

            return item;
        }

        private TaskItem CreateTask(Priority priority)
        {
            var item = new TaskItem(priority);

            item.OnCompleted = () =>
            {
                var count = this._counter.Decrement();

                var processNext = count < this._counter.Limit;

                this.NotifyItemProcessed(item, count);

                if (processNext)
                {
                    this._taskQueue.Dequeue()?.Process();
                }
            };

            return item;
        }

        private void NotifyItemRejected(TaskItem item, string reason)
            => this._events?.Rejected?.Raise(new ItemRejectedEventArgs(
                item.Priority,
                reason));

        private void NotifyItemProcessed(TaskItem item, int count)
            => this._events?.ItemProcessed?.Raise(new ItemProcessedEventArgs(
                item.Priority,
                item.ProcessingTime,
                this.ConcurrencyLimit,
                count));

        private void NotifyItemProcessing(TaskItem item, int count)
            => this._events?.ItemProcessing?.Raise(new ItemProcessingEventArgs(
                item.Priority,
                this.ConcurrencyLimit,
                count));

        private void NotifyConcurrencyLimitChanged()
            => this._events?.ConcurrencyLimitChanged?.Raise(new LimitChangedEventArgs(this._counter.Limit));

        private void NotifyItemDequeued(int count, TaskItem item) => this._events?.ItemDequeued?.Raise(new ItemDequeuedEventArgs(
             item.Priority,
             item.WaitingTime,
             this._taskQueue.Limit,
             count));

        private void NotifyItemEnqueued(int count, TaskItem item) => this._events?.ItemEnqueued?.Raise(new ItemEnqueuedEventArgs(
            item.Priority,
            this._taskQueue.Limit,
            count));

        private void ProcessPendingTasks()
        {
            while (this._counter.AvailableCount > 0)
            {
                var item = this._taskQueue.Dequeue();

                if (item == null)
                {
                    break;
                }

                item.Process();
            }
        }
    }
}

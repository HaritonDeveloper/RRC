
using System;
using RRC.Abstractions;

namespace RRC.Student;

public sealed class LucasBackoffPolicy : IRequestPolicy
{
    // Базовый тайм-аут запроса.
    private const double BaseTimeoutMs = 150.0;

    // Минимальный и максимальный тайм-аут.
    private const double MinTimeoutMs = 120.0;
    private const double MaxTimeoutMs = 600.0;

    // Параметры Backoff.
    private const double BaseDelayMs = 80.0;
    private const double MaxDelayMs = 2000.0;

    // Максимальное количество повторных попыток.
    private const int MaxRetries = 6;

    // Коэффициент сглаживания статистики.
    private const double Alpha = 0.15;

    // Границы jitter.
    private const double JitterMin = 0.8;
    private const double JitterMax = 1.2;

    private readonly object sync = new();

    // Скользящая оценка частоты тайм-аутов.
    private double timeoutRate = 0.0;

    // Текущий адаптивный тайм-аут.
    private double currentTimeoutMs = BaseTimeoutMs;

    private bool initialized = false;

    public TimeSpan GetTimeout(
        RequestContext context,
        Random random)
    {
        lock (sync)
        {
            return TimeSpan.FromMilliseconds(currentTimeoutMs);
        }
    }

    public RetryDecision OnResult(
        RequestObservation observation,
        Random random)
    {
        lock (sync)
        {
            bool timedOut =
                observation.Outcome == RequestOutcome.Timeout;

            UpdateTimeout(timedOut);

            // Успешно завершившийся запрос повторять не нужно.
            if (!timedOut)
            {
                return RetryDecision.NoRetry();
            }

            // Ограничиваем число повторных попыток.
            if (observation.Attempt >= MaxRetries + 1)
            {
                return RetryDecision.NoRetry();
            }

            // Рассчитываем число Люка.
            long lucas = GetLucas(observation.Attempt);

            // Рассчитываем случайный коэффициент.
            double jitter =
                JitterMin +
                random.NextDouble() * (JitterMax - JitterMin);

            // Рассчитываем задержку.
            double delayMs = BaseDelayMs * lucas * jitter;

            // Ограничиваем максимальную задержку.
            delayMs = Math.Min(delayMs, MaxDelayMs);

            return RetryDecision.RetryAfter(
                TimeSpan.FromMilliseconds(delayMs));
        }
    }

    private void UpdateTimeout(bool timedOut)
    {
        double sample = timedOut ? 1.0 : 0.0;

        if (!initialized)
        {
            timeoutRate = sample;
            initialized = true;
        }
        else
        {
            timeoutRate =
                Alpha * sample +
                (1.0 - Alpha) * timeoutRate;
        }

        // При росте количества тайм-аутов
        // увеличиваем время ожидания.
        double targetTimeout =
            BaseTimeoutMs * (1.0 + 2.0 * timeoutRate);

        targetTimeout = Math.Clamp(
            targetTimeout,
            MinTimeoutMs,
            MaxTimeoutMs);

        // Плавное изменение тайм-аута.
        currentTimeoutMs =
            0.7 * currentTimeoutMs +
            0.3 * targetTimeout;
    }

    private static long GetLucas(int index)
    {
        if (index <= 0)
            return 2;

        if (index == 1)
            return 1;

        long previous = 2;
        long current = 1;

        for (int i = 2; i <= index; i++)
        {
            long next = previous + current;

            previous = current;
            current = next;
        }

        return current;
    }
}

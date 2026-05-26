import { test, expect } from '@playwright/test';
import { ADMIN_STATE } from './helpers';

test.use({ storageState: ADMIN_STATE });

test.describe('Parking', () => {
  test('leaderboard shows the hero score card and ranked tables', async ({ page }) => {
    await page.goto('/parking/leaderboard');
    await expect(page.locator('.tier-ring')).toBeVisible();
    await expect(page.locator('.hero__points')).toBeVisible();
    await expect(page.locator('.trust-meter')).toBeVisible();
    await expect(page.locator('.admin-panel').first()).toBeVisible();
  });

  test('reserving a future window round-trips the picked time', async ({ page }) => {
    await page.goto('/parking');
    // Wait for the InteractiveServer circuit to hydrate, otherwise @bind doesn't
    // capture the filled values and they snap back to the defaults.
    const bar = page.locator('.booking-bar__row');
    await expect(bar.locator('input[type="date"]')).toBeVisible();
    await page.waitForTimeout(1500);

    // A few days out (so it is never "in the past") at a two-digit hour and a
    // randomised day so re-runs don't collide on an already-booked spot.
    const day = new Date(Date.now() + (2 + Math.floor(Math.random() * 8)) * 86_400_000);
    const iso = day.toISOString().slice(0, 10); // yyyy-MM-dd
    const hour = 10 + Math.floor(Math.random() * 5); // 10..14
    const hh = String(hour).padStart(2, '0');

    const dateInput = bar.locator('input[type="date"]');
    await dateInput.fill(iso);
    await dateInput.blur();
    await bar.locator('input[type="time"]').nth(0).fill(`${hh}:00`);
    await bar.locator('input[type="time"]').nth(0).blur();
    await bar.locator('input[type="time"]').nth(1).fill(`${hh}:30`);
    await bar.locator('input[type="time"]').nth(1).blur();
    // Confirm the binding took before searching.
    await expect(dateInput).toHaveValue(iso);
    await page.getByRole('button', { name: /Najít/ }).click();

    const firstSpot = page.locator('.spot-card').first();
    await expect(firstSpot).toBeVisible();
    await firstSpot.getByRole('button', { name: /Rezervovat/ }).click();

    // The window is stored as the picked wall-clock in UTC, so the list must show
    // exactly the picked time — this guards the timezone (timestamptz) regression.
    const csDate = `${iso.slice(8, 10)}.${iso.slice(5, 7)}.${iso.slice(0, 4)}`; // dd.MM.yyyy
    await expect(page.getByText(new RegExp(`${csDate} ${hour}:00`))).toBeVisible();
  });
});

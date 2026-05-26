import { test, expect } from '@playwright/test';
import { ADMIN_STATE } from './helpers';

test.use({ storageState: ADMIN_STATE });

test.describe('Home dashboard', () => {
  test('shows the hero and permission-gated quick-action tiles', async ({ page }) => {
    await page.goto('/');
    await expect(page.locator('.home-hero__title')).toHaveText('Webora');
    const tiles = page.locator('.home-tile');
    await expect(tiles.first()).toBeVisible();
    // Admin sees parking + the admin areas.
    expect(await tiles.count()).toBeGreaterThan(3);
  });

  test('a quick-action tile navigates to its page', async ({ page }) => {
    await page.goto('/');
    await page.locator('.home-tile', { hasText: 'Žebříček' }).click();
    await expect(page).toHaveURL(/parking\/leaderboard/);
  });
});

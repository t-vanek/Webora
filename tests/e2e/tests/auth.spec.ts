import { test, expect } from '@playwright/test';
import { ADMIN, fillFluentField, login, submitForm } from './helpers';

// These run signed out.
test.use({ storageState: { cookies: [], origins: [] } });

test.describe('Authentication', () => {
  test('login page shows the split brand panel and the form', async ({ page }) => {
    await page.goto('/login');
    await expect(page.locator('.auth-shell__brand')).toBeVisible();
    await expect(page.locator('fluent-text-field[name="Input.Email"]')).toBeVisible();
    await expect(page.locator('fluent-text-field[name="Input.Password"]')).toBeVisible();
  });

  test('invalid credentials show an error', async ({ page }) => {
    await login(page, ADMIN.email, 'WrongPassword1!');
    await expect(page.getByText(/Neplatný e-mail nebo heslo|Invalid/i)).toBeVisible();
  });

  test('valid credentials sign in and land on the dashboard', async ({ page }) => {
    await login(page);
    await expect(page.locator('.wallet-chip')).toBeVisible();
    await expect(page).toHaveURL(/\/$/);
    await expect(page.locator('.home-hero__title')).toBeVisible();
  });

  test('registration creates an account and shows the activation notice', async ({ page }) => {
    await page.goto('/register');
    const email = `e2e+${Date.now()}@example.com`;
    await fillFluentField(page, 'Input.DisplayName', 'E2E Test');
    await fillFluentField(page, 'Input.Email', email);
    await fillFluentField(page, 'Input.Password', 'Heslo12345!');
    await fillFluentField(page, 'Input.ConfirmPassword', 'Heslo12345!');
    await submitForm(page);
    await expect(page.getByText(/aktivační odkaz|activation link/i)).toBeVisible();
  });
});

import path from 'node:path'
import tailwindcss from '@tailwindcss/vite'
import { tanstackRouter } from '@tanstack/router-plugin/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite-plus'
import { reactDoctorRules } from './lint.rules'
import pkg from './package.json' with { type: 'json' }

// The one configuration file for the whole toolchain (ADR-0004): dev server and build, oxlint and
// oxfmt. `vp dev` serves the UI on 5173 and proxies /api to the server `dotnet run` starts; `vp
// build` writes into the server's wwwroot, so one `dotnet run` then serves the API and the UI
// together. The router plugin must come before the React plugin: it generates routeTree.gen.ts from
// src/routes, and React Fast Refresh has to see the generated file.
export default defineConfig({
  // The sidebar names the bundle the browser is running. Taken from package.json so there is one
  // place the number is written, and inlined so no request is spent on a constant.
  define: { APP_VERSION: JSON.stringify(pkg.version) },
  plugins: [
    tanstackRouter({ target: 'react', autoCodeSplitting: true }),
    // React Compiler through `oxc-transform-react`, the Rust port, rather than the Babel plugin:
    // Babel is the only thing this toolchain would otherwise have to install. The plugin calls its
    // native compiler support experimental, and a fatal diagnostic fails the transform loudly rather
    // than silently miscompiling, so the failure mode is a build error.
    react({ compiler: true }),
    tailwindcss(),
  ],
  resolve: {
    alias: { '@': path.resolve(import.meta.dirname, 'src') },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:5000', changeOrigin: true },
    },
  },
  build: {
    outDir: '../CodeExplorer/wwwroot',
    emptyOutDir: true,
  },
  lint: {
    // routeTree.gen.ts is written by the router plugin on every run; linting it would only ever
    // report on generated code nobody edits.
    ignorePatterns: ['src/routeTree.gen.ts', 'dist'],
    // `unicorn` and `oxc` are on by default and are named here because listing plugins replaces that
    // default. `react-perf` is deliberately absent: its rules forbid the inline handlers and object
    // props that React Compiler exists to memoize, so it would argue with the compiler.
    plugins: ['import', 'jsx-a11y', 'node', 'oxc', 'promise', 'react', 'typescript', 'unicorn'],
    // React Doctor's rules, for the security, correctness and accessibility checks the native
    // plugins have no equivalent of. See lint.rules.ts for which of its 906 rules are on and why.
    jsPlugins: [{ name: 'react-doctor', specifier: 'oxlint-plugin-react-doctor' }],
    // Every category that finds real defects is an error. `pedantic` is off on purpose: on a React
    // codebase it is mostly max-lines-per-function and max-dependencies on components that are long
    // because JSX is long, and `style` overlaps with what oxfmt already decides.
    categories: { correctness: 'error', perf: 'error', suspicious: 'error' },
    rules: {
      ...reactDoctorRules,
      // `jsx: 'react-jsx'` makes the compiler import the factory itself. The rule predates the
      // automatic runtime and would otherwise fire on every element in the app.
      'react/react-in-jsx-scope': 'off',
      'react-doctor/react-in-jsx-scope': 'off',
      // The Tailwind entry is imported for its side effect; that is how a Vite CSS entry is written.
      'import/no-unassigned-import': 'off',
    },
    // The one boundary the folder layout rests on, checked rather than remembered — the server has a
    // test for the same rule between its modules. `components/` is what every feature uses and
    // `lib/` is the HTTP client, the URL contracts and the query-key roots: a feature may reach into
    // either, and neither may reach back, or the shared half of the app depends on the half that is
    // allowed to change. The app frame lives in `src/app/` precisely because it does depend on
    // features, and is deliberately outside this list.
    overrides: [
      {
        files: ['src/components/**', 'src/lib/**'],
        rules: {
          'no-restricted-imports': [
            'error',
            {
              patterns: [
                {
                  group: ['@/features/*', '@/features/**'],
                  message:
                    'components/ and lib/ are shared by every feature and may not depend on one. Move what is shared into lib/ (a URL contract, the HTTP client) or components/, declare the fields a shared component renders as its own prop type rather than importing a feature response shape, or move the file into src/app/, which may depend on features.',
                },
              ],
            },
          ],
        },
      },
    ],
  },
  fmt: {
    // Excluded for the reason the lint list excludes it, and because the file itself asks to be:
    // the router plugin rewrites it on every run, so oxfmt and the generator disagree permanently —
    // `vp check` reported it unformatted and `vp check --fix` then rewrote it to the same bytes,
    // which is a gate no commit could ever pass.
    ignorePatterns: ['src/routeTree.gen.ts'],
    semi: false,
    singleQuote: true,
  },
})

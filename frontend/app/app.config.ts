/*
 * Nuxt UI restyled as drafting furniture: square corners, hairline rules instead of shadows,
 * labels in tracked capitals. The colors themselves live in main.css.
 */
export default defineAppConfig({
  ui: {
    colors: {
      primary: 'neutral',
      neutral: 'stone'
    },
    button: {
      slots: {
        base: 'rounded-none font-semibold uppercase tracking-[0.12em]'
      },
      variants: {
        size: {
          xs: { base: 'px-2 py-1 text-[10px]' },
          sm: { base: 'px-2.5 py-1.5 text-[10.5px]' },
          md: { base: 'px-3 py-2 text-[11px]' },
          lg: { base: 'px-4 py-2.5 text-xs' },
          xl: { base: 'px-5 py-3 text-xs' }
        }
      },
      defaultVariants: {
        color: 'neutral',
        variant: 'outline'
      }
    },
    input: {
      slots: {
        base: 'rounded-none font-mono'
      }
    },
    textarea: {
      slots: {
        base: 'rounded-none font-mono'
      }
    },
    formField: {
      slots: {
        label: 'caps text-muted',
        help: 'mt-1.5 text-muted text-xs leading-snug',
        error: 'mt-1.5 text-error text-xs'
      }
    },
    switch: {
      slots: {
        base: 'rounded-none',
        thumb: 'rounded-none shadow-none',
        label: 'block font-mono text-default',
        description: 'text-muted text-xs leading-snug'
      }
    },
    badge: {
      slots: {
        base: 'rounded-none font-semibold uppercase tracking-[0.1em]'
      }
    },
    alert: {
      slots: {
        root: 'rounded-none',
        title: 'caps',
        description: 'text-sm leading-snug'
      }
    },
    modal: {
      slots: {
        overlay: 'bg-desk/70',
        content: 'rounded-none shadow-none ring ring-accented divide-y divide-default',
        header: 'min-h-12 px-4 sm:px-5 py-2.5 bg-accented',
        title: 'caps text-highlighted',
        description: 'mt-1 text-muted text-xs',
        body: 'p-4 sm:p-5',
        footer: 'px-4 sm:px-5 py-3',
        close: 'absolute top-2 end-2'
      }
    },
    toast: {
      slots: {
        root: 'rounded-none shadow-none ring ring-accented',
        title: 'caps text-highlighted',
        description: 'text-sm text-muted'
      }
    },
    tooltip: {
      slots: {
        content: 'rounded-none shadow-none ring ring-accented'
      }
    }
  }
})

.[:300]
| walk(if type == "object" then with_entries(select(.key != "id")) else . end) as $w
| {
    numbers: ([$w | .. | numbers] | add),
    paths: ([$w | paths(scalars)] | length)
  }
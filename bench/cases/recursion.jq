def sum_to: if . == 0 then 0 else . + (. - 1 | sum_to) end;
[range(300) | sum_to] | add

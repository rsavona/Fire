      

```
 "DecisionPoints":  "SORT1:0;PNA2 151:1000|PNA2 152:2000;PNA2_Verify:1000",
```
Tells the Virtual Plc to Simulate a carton going through the list of Decision points.
Delimited by ; when a | is encountered it will roound robin between the ORed points
each point is of the format: Decision PT : time delay ms


       